using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.NET.Sdk.Razor.SourceGenerators;
using Roslyn.Test.Utilities;
using System.Collections.Immutable;
using System.Text;

namespace DotNetInternals.RazorAccess;

public static class RazorCompiler
{
    public static readonly InitialCode InitialRazorCode = new("TestComponent.razor", """
        <TestComponent Param="1" />

        @code {
            [Parameter] public int Param { get; set; }
        }
        """);

    public static readonly InitialCode InitialCSharpCode = new("Class.cs", """
        class Class
        {
            public void M()
            {
            }
        }
        """);

    public static CompiledAssembly Compile(IEnumerable<InputCode> inputs)
    {
        var directory = "/TestProject/";
        var fileSystem = new VirtualRazorProjectFileSystem();
        var cSharp = new List<SyntaxTree>();
        foreach (var input in inputs)
        {
            var filePath = directory + input.FileName;
            switch (input.FileExtension)
            {
                case ".razor":
                    {
                        var item = new SourceGeneratorProjectItem(
                            basePath: "/",
                            filePath: filePath,
                            relativePhysicalPath: input.FileName,
                            fileKind: FileKinds.Component,
                            additionalText: new TestAdditionalText(input.Text, encoding: Encoding.UTF8, path: filePath),
                            cssScope: null);
                        fileSystem.Add(item);
                        break;
                    }
                case ".cs":
                    {
                        cSharp.Add(CSharpSyntaxTree.ParseText(input.Text, path: filePath));
                        break;
                    }
            }
        }

        var config = RazorConfiguration.Default;

        // Phase 1: Declaration only (to be used as a reference from which tag helpers will be discovered).
        RazorProjectEngine declarationProjectEngine = createProjectEngine([]);
        var declarationCompilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: [
                ..fileSystem.EnumerateItems("/").Select((item) =>
                {
                    RazorCodeDocument declarationCodeDocument = declarationProjectEngine.ProcessDeclarationOnly(item);
                    string declarationCSharp = declarationCodeDocument.GetCSharpDocument().GeneratedCode;
                    return CSharpSyntaxTree.ParseText(declarationCSharp);
                }),
                ..cSharp,
            ],
            Basic.Reference.Assemblies.AspNet80.References.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Phase 2: Full generation.
        RazorProjectEngine projectEngine = createProjectEngine([
            ..Basic.Reference.Assemblies.AspNet80.References.All,
            declarationCompilation.ToMetadataReference()]);
        var compiledFiles = fileSystem.EnumerateItems("/")
            .ToImmutableDictionary(
                keySelector: (item) => item.RelativePhysicalPath,
                elementSelector: (item) =>
                {
                    RazorCodeDocument codeDocument = projectEngine.Process(item);

                    string syntax = codeDocument.GetSyntaxTree().Root.SerializedValue;

                    string ir = formatDocumentTree(codeDocument.GetDocumentIntermediateNode());

                    string cSharp = codeDocument.GetCSharpDocument().GeneratedCode;

                    return new CompiledRazorFile(
                        Syntax: syntax,
                        Ir: ir,
                        CSharp: cSharp);
                });

        var finalCompilation = CSharpCompilation.Create("TestAssembly",
            compiledFiles.Values.Select((file) => CSharpSyntaxTree.ParseText(file.CSharp)),
            Basic.Reference.Assemblies.AspNet80.References.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = finalCompilation
            .GetDiagnostics()
            .Where(d => d.Severity != DiagnosticSeverity.Hidden);
        string diagnosticsText = getActualDiagnosticsText(diagnostics);

        return new CompiledAssembly(
            Files: compiledFiles,
            Diagnostics: diagnosticsText,
            NumWarnings: diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
            NumErrors: diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));

        RazorProjectEngine createProjectEngine(IReadOnlyList<MetadataReference> references)
        {
            return RazorProjectEngine.Create(config, fileSystem, b =>
            {
                b.SetRootNamespace("TestNamespace");

                b.Features.Add(new DefaultTypeNameFeature());
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

        static string formatDocumentTree(DocumentIntermediateNode node)
        {
            var formatter = new DebuggerDisplayFormatter();
            formatter.FormatTree(node);
            return formatter.ToString();
        }

        static string getActualDiagnosticsText(IEnumerable<Diagnostic> diagnostics)
        {
            var assertText = DiagnosticDescription.GetAssertText(
            expected: [],
            actual: diagnostics,
            unmatchedExpected: [],
            unmatchedActual: diagnostics);
            var startAnchor = "Actual:" + Environment.NewLine;
            var endAnchor = "Diff:" + Environment.NewLine;
            var start = assertText.IndexOf(startAnchor, StringComparison.Ordinal) + startAnchor.Length;
            var end = assertText.IndexOf(endAnchor, start, StringComparison.Ordinal);
            var result = assertText[start..end];
            return removeIndentation(result);
        }

        static string removeIndentation(string text)
        {
            var spaces = new string(' ', 16);
            return text.Trim().Replace(Environment.NewLine + spaces, Environment.NewLine);
        }
    }
}

public record InitialCode(string SuggestedFileName, string TextTemplate)
{
    public string SuggestedFileNameWithoutExtension => Path.GetFileNameWithoutExtension(SuggestedFileName);
    public string SuggestedFileExtension => Path.GetExtension(SuggestedFileName);

    public string GetFinalFileName(string suffix)
    {
        return string.IsNullOrEmpty(suffix)
            ? SuggestedFileName
            : SuggestedFileNameWithoutExtension + suffix + SuggestedFileExtension;
    }

    public InputCode ToInputCode(string? finalFileName = null)
    {
        finalFileName ??= SuggestedFileName;

        var original = SuggestedFileNameWithoutExtension;
        var replacement = Path.GetFileNameWithoutExtension(finalFileName);

        return new(
            finalFileName,
            finalFileName == SuggestedFileName
                ? TextTemplate
                : TextTemplate.Replace(original, replacement, StringComparison.Ordinal));
    }
}

public record InputCode(string FileName, string Text)
{
    public string FileExtension => Path.GetExtension(FileName);
}

public record CompiledAssembly(ImmutableDictionary<string, CompiledRazorFile> Files, string Diagnostics, int NumWarnings, int NumErrors);

public record CompiledRazorFile(string Syntax, string Ir, string CSharp);
