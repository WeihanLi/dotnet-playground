#!/bin/sh
curl -sSL https://dot.net/v1/dotnet-install.sh > dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --jsonfile global.json --install-dir ./dotnet
./dotnet/dotnet --version
./dotnet/dotnet workload install wasm-tools wasm-experimental
./dotnet/dotnet publish -p:CompressionEnabled=false -o output src/App
