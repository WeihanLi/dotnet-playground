using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Text;

namespace DotNetInternals;

public class Compiler : ICompiler
{
    public async Task<CompiledAssembly> CompileAsync(IEnumerable<InputCode> inputs)
    {
        var directory = "/TestProject/";
        var fileSystem = new VirtualRazorProjectFileSystemProxy();
        var cSharp = new Dictionary<string, CSharpSyntaxTree>();
        foreach (var input in inputs)
        {
            var filePath = directory + input.FileName;
            switch (input.FileExtension)
            {
                case ".razor":
                case ".cshtml":
                    {
                        var item = RazorAccessors.CreateSourceGeneratorProjectItem(
                            basePath: "/",
                            filePath: filePath,
                            relativePhysicalPath: input.FileName,
                            fileKind: null!, // will be automatically determined from file path
                            additionalText: new TestAdditionalText(input.Text, encoding: Encoding.UTF8, path: filePath),
                            cssScope: null);
                        fileSystem.Add(item);
                        break;
                    }
                case ".cs":
                    {
                        cSharp[input.FileName] = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(input.Text, path: filePath);
                        break;
                    }
            }
        }

        // Choose output kind EXE if there are top-level statements, otherwise DLL.
        var outputKind = cSharp.Values.Any(tree => tree.GetRoot().DescendantNodes().OfType<GlobalStatementSyntax>().Any())
            ? OutputKind.ConsoleApplication
            : OutputKind.DynamicallyLinkedLibrary;

        var options = new CSharpCompilationOptions(
            outputKind,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable,
            concurrentBuild: false);

        var config = RazorConfiguration.Default;

        var references = Basic.Reference.Assemblies.AspNet90.References.All;

        // Phase 1: Declaration only (to be used as a reference from which tag helpers will be discovered).
        RazorProjectEngine declarationProjectEngine = createProjectEngine([]);
        var declarationCompilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: [
                ..fileSystem.Inner.EnumerateItems("/").Select((item) =>
                {
                    RazorCodeDocument declarationCodeDocument = declarationProjectEngine.ProcessDeclarationOnly(item);
                    string declarationCSharp = declarationCodeDocument.GetCSharpDocument().GeneratedCode;
                    return CSharpSyntaxTree.ParseText(declarationCSharp);
                }),
                ..cSharp.Values,
            ],
            references,
            options);

        // Phase 2: Full generation.
        RazorProjectEngine projectEngine = createProjectEngine([
            ..references,
            declarationCompilation.ToMetadataReference()]);
        var compiledRazorFiles = fileSystem.Inner.EnumerateItems("/")
            .ToImmutableDictionary(
                keySelector: (item) => item.RelativePhysicalPath,
                elementSelector: (item) =>
                {
                    RazorCodeDocument codeDocument = projectEngine.Process(item);
                    RazorCodeDocument designTimeDocument = projectEngine.ProcessDesignTime(item);

                    string syntax = codeDocument.GetSyntaxTree().Serialize();
                    string ir = codeDocument.GetDocumentIntermediateNode().Serialize();
                    string cSharp = codeDocument.GetCSharpDocument().GeneratedCode;

                    string designSyntax = designTimeDocument.GetSyntaxTree().Serialize();
                    string designIr = designTimeDocument.GetDocumentIntermediateNode().Serialize();
                    string designCSharp = designTimeDocument.GetCSharpDocument().GeneratedCode;

                    return new CompiledFile([
                        new() { Type = "Syntax", Text = syntax, DesignTimeText = designSyntax },
                        new() { Type = "IR", Text = ir, DesignTimeText = designIr },
                        new() { Type = "C#", Text = cSharp, DesignTimeText = designCSharp, Priority = 1 },
                    ]);
                });

        var finalCompilation = CSharpCompilation.Create("TestAssembly",
            [
                ..compiledRazorFiles.Values.Select(static (file) =>
                {
                    var cSharpText = file.GetOutput("C#")!.Text!;
                    return CSharpSyntaxTree.ParseText(cSharpText);
                }),
                ..cSharp.Values,
            ],
            references,
            options);

        ICSharpCode.Decompiler.Metadata.PEFile? peFile = null;

        var compiledFiles = compiledRazorFiles.AddRange(
            await cSharp.SelectAsync(async (pair) => new KeyValuePair<string, CompiledFile>(
                pair.Key,
                new([
                    new() { Type = "Syntax", Text = pair.Value.GetRoot().Dump() },
                    new() { Type = "IL", Text = get(() =>
                    {
                        peFile ??= getPeFile(finalCompilation);
                        return getIl(peFile);
                    }) },
                    new() { Type = "C#", Text = await getAsync(async () =>
                    {
                        peFile ??= getPeFile(finalCompilation);
                        return await getCSharpAsync(peFile);
                    }) },
                    new() { Type = "Run", Text = get(() =>
                    {
                        var executableCompilation = finalCompilation.Options.OutputKind == OutputKind.ConsoleApplication
                            ? finalCompilation
                            : finalCompilation.WithOptions(finalCompilation.Options.WithOutputKind(OutputKind.ConsoleApplication));
                        var emitStream = getEmitStream(executableCompilation);
                        return emitStream is null
                            ? executableCompilation.GetDiagnostics().FirstOrDefault(d => d.Id == "CS5001") is { } error
                                ? error.GetMessage(CultureInfo.InvariantCulture)
                                : "Cannot execute due to compilation errors."
                            : Executor.Execute(emitStream);
                    }),
                        Priority = 1
                    },
                ]))));

        IEnumerable<Diagnostic> diagnostics = getEmitDiagnostics(finalCompilation)
            .Where(d => d.Severity != DiagnosticSeverity.Hidden);
        string diagnosticsText = diagnostics.GetDiagnosticsText();
        int numWarnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int numErrors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        ImmutableArray<DiagnosticData> diagnosticData = diagnostics
            .Select(d => d.ToDiagnosticData())
            .ToImmutableArray();

        var result = new CompiledAssembly(
            BaseDirectory: directory,
            Files: compiledFiles,
            NumWarnings: numWarnings,
            NumErrors: numErrors,
            Diagnostics: diagnosticData,
            GlobalOutputs:
            [
                new()
                {
                    Type = CompiledAssembly.DiagnosticsOutputType,
                    Text = diagnosticsText,
                    Priority = numErrors > 0 ? 2 : 0,
                },
            ]);

        return result;

        // TODO: Make the outputs lazy again and remove this.
        Task<string> getAsync(Func<Task<string>> factory) => factory();
        string get(Func<string> factory) => factory();

        RazorProjectEngine createProjectEngine(IReadOnlyList<MetadataReference> references)
        {
            return RazorProjectEngine.Create(config, fileSystem.Inner, b =>
            {
                b.SetRootNamespace("TestNamespace");

                b.Features.Add(RazorAccessors.CreateDefaultTypeNameFeature());
                b.Features.Add(new CompilationTagHelperFeature());
                b.Features.Add(new DefaultMetadataReferenceFeature
                {
                    References = references,
                });

                CompilerFeatures.Register(b);
                RazorExtensions.Register(b);

                b.SetCSharpLanguageVersion(LanguageVersion.Preview);
            });
        }

        static MemoryStream? getEmitStream(CSharpCompilation compilation)
        {
            var stream = new MemoryStream();
            var emitResult = compilation.Emit(stream);
            if (!emitResult.Success)
            {
                return null;
            }

            stream.Position = 0;
            return stream;
        }

        static ImmutableArray<Diagnostic> getEmitDiagnostics(CSharpCompilation compilation)
        {
            var emitResult = compilation.Emit(new MemoryStream());
            return emitResult.Diagnostics;
        }

        static ICSharpCode.Decompiler.Metadata.PEFile? getPeFile(CSharpCompilation compilation)
        {
            return getEmitStream(compilation) is { } stream
                ? new(compilation.AssemblyName ?? "", stream)
                : null;
        }

        static string getIl(ICSharpCode.Decompiler.Metadata.PEFile? peFile)
        {
            if (peFile is null)
            {
                return "";
            }

            var output = new ICSharpCode.Decompiler.PlainTextOutput();
            var disassembler = new ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler(output, cancellationToken: default);
            disassembler.WriteModuleContents(peFile);
            return output.ToString();
        }

        static async Task<string> getCSharpAsync(ICSharpCode.Decompiler.Metadata.PEFile? peFile)
        {
            if (peFile is null)
            {
                return "";
            }

            var typeSystem = await ICSharpCode.Decompiler.TypeSystem.DecompilerTypeSystem.CreateAsync(
                peFile,
                new ICSharpCode.Decompiler.Metadata.UniversalAssemblyResolver(
                    mainAssemblyFileName: null,
                    throwOnError: false,
                    targetFramework: ".NETCoreApp,Version=9.0"));
            var decompiler = new ICSharpCode.Decompiler.CSharp.CSharpDecompiler(
                typeSystem,
                new ICSharpCode.Decompiler.DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp1));
            return decompiler.DecompileWholeModuleAsString();
        }
    }
}

internal sealed class TestAdditionalText(string path, SourceText text) : AdditionalText
{
    public TestAdditionalText(string text = "", Encoding? encoding = null, string path = "dummy")
        : this(path, SourceText.From(text, encoding))
    {
    }

    public override string Path => path;

    public override SourceText GetText(CancellationToken cancellationToken = default) => text;
}
