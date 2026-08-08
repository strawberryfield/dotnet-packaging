# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a .NET packaging solution that provides command-line extensions for the .NET Core CLI to create deployment packages:
- **dotnet-zip** - Create `.zip` files
- **dotnet-tarball** - Create `.tar.gz` files  
- **dotnet-rpm** - Create RPM packages (`.rpm`)
- **dotnet-deb** - Create Debian/Ubuntu packages (`.deb`)

The core logic lives in `Packaging.Targets` (a NuGet package with MSBuild tasks), consumed by the individual CLI tools.

## Architecture

```
dotnet-packaging.sln
├── Packaging.Targets/              # Core MSBuild tasks library (netstandard2.0)
│   ├── build/Packaging.Targets.targets   # MSBuild targets (CreateZip, CreateTarball, CreateRpm, CreateDeb)
│   ├── Deb/                          # Deb package creation logic
│   ├── IO/                           # Archive I/O (tar, cpio, ar, xz, gzip)
│   ├── Native/                       # Platform-specific native interop
│   ├── Rpm/                          # RPM package creation logic
│   └── *.cs                          # Shared tasks (ZipTask, TarballTask, RpmTask, DebTask)
├── Packaging.Targets.Tests/          # xUnit tests (multi-target: net6.0-net10.0)
├── dotnet-zip/                       # CLI tool (net6.0-net10.0)
├── dotnet-tarball/                   # CLI tool (net6.0-net10.0)
├── dotnet-rpm/                       # CLI tool (net6.0-net10.0) - contains PackagingRunner.cs
├── dotnet-deb/                       # CLI tool (net6.0-net10.0)
└── molecule/                         # Integration tests with Ansible/Molecule
```

### Key Components

1. **PackagingRunner.cs** (in `dotnet-rpm`, linked to others) - Entry point for all CLI tools. Handles argument parsing, MSBuild invocation, and `install` command to add `Packaging.Targets` PackageReference.

2. **Packaging.Targets.targets** - Defines MSBuild targets:
   - `CreatePackageProperties` - Computes package version, name, paths
   - `CreateZip` / `CreateTarball` / `CreateRpm` / `CreateDeb` - Package creation targets
   - `Msi` - WiX-based MSI creation (Windows only)

3. **Tasks** (`*.cs` in Packaging.Targets):
   - `ZipTask` - Creates .zip archives
   - `TarballTask` - Creates .tar.gz archives
   - `RpmTask` - Creates RPM packages via cpio
   - `DebTask` - Creates DEB packages via ar/tar/xz

4. **Archive I/O** (`IO/`): Custom implementations for tar, cpio, ar, xz, gzip formats with native LZMA support on Windows.

## Common Commands

### Build
```bash
# Restore and build solution
dotnet restore dotnet-packaging.sln
dotnet build dotnet-packaging.sln -c Release

# Pack NuGet packages
dotnet pack dotnet-packaging.sln -c Release -o pkg/
```

### Test
```bash
# Run all tests (multi-target)
dotnet test Packaging.Targets.Tests/Packaging.Targets.Tests.csproj

# Run tests for specific framework
dotnet test Packaging.Targets.Tests/Packaging.Targets.Tests.csproj -f net8.0

# Run single test class
dotnet test Packaging.Targets.Tests/Packaging.Targets.Tests.csproj --filter "FullyQualifiedName~DebPackageCreatorTests"

# Run single test method
dotnet test Packaging.Targets.Tests/Packaging.Targets.Tests.csproj --filter "FullyQualifiedName~DebPackageCreatorTests.CreateDebPackage_WithContent"
```

### Local Development
```bash
# Pack locally and test in a demo project
dotnet pack dotnet-packaging.sln -c Release -o ./local-packages

# In a test project directory:
dotnet new console -n TestApp
cd TestApp
dotnet tool install --global dotnet-deb --version <version> --add-source ../local-packages
dotnet deb install
dotnet deb
```

### CI Pipeline (GitHub Actions)
```bash
# The CI builds on Ubuntu with .NET 6.0-10.0, runs tests, publishes artifacts
# See .github/workflows/main.yml
```

### Azure Pipelines
- Builds on multiple Linux containers (3.1 through 10.0) + Windows
- Runs molecule integration tests for framework-dependent and self-contained apps
- Publishes to NuGet on master branch

## Key Patterns

### Adding a New Package Format
1. Create new task in `Packaging.Targets/` (e.g., `PkgTask.cs`)
2. Add `UsingTask` in `Packaging.Targets.targets`
3. Add new target (e.g., `CreatePkg`) with properties and task invocation
4. Create new CLI tool project referencing `PackagingRunner.cs`

### Versioning
- Uses NerdBank.GitVersioning (`version.json`)
- Version derived from git history; releases from `master` branch or `vN.N` tags
- `Directory.Build.props` configures common properties

### Native Dependencies
- LZMA (`runtimes/win7-x64/native/lzma.dll`) for xz compression on Windows
- BouncyCastle and SharpZipLib bundled in package tools folder

## Important Files

| File | Purpose |
|------|---------|
| `Packaging.Targets/build/Packaging.Targets.targets` | Main MSBuild targets |
| `dotnet-rpm/PackagingRunner.cs` | Shared CLI entry point |
| `Packaging.Targets/AssemblyAttributes.cs` | Assembly metadata (via NerdBank.GitVersioning) |
| `.github/workflows/main.yml` | GitHub Actions CI |
| `.azure-pipelines.yml` | Azure Pipelines CI |
| `version.json` | Versioning configuration |

## Testing Notes

- Tests use xUnit with Moq
- Test data files in `Packaging.Targets.Tests/` subdirectories (Deb, IO, Rpm)
- Run with `dotnet test` - no special test runner needed
- Multi-targeted: net6.0, net7.0, net8.0, net9.0, net10.0