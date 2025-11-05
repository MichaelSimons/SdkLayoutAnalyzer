# SDK Layout Analyzer

A tool for analyzing duplicate files in .NET SDK distributions to identify opportunities for size reduction and optimization.

## Overview

The SDK Layout Analyzer examines .NET SDK archives (zip or tar.gz) and identifies duplicate files based on:

- **Filename** - The name of the file
- **Target Framework Moniker (TFM)** - The .NET framework version the file targets
- **Culture** - The localization culture for resource assemblies

This analysis helps identify opportunities to reduce SDK size by consolidating duplicate dependencies into shared locations.

## Features

- **Duplicate Detection**: Identifies files duplicated across the SDK layout
- **Smart File Selection**: Determines which duplicate to keep based on:
  - Lowest version (for better compatibility)
  - Largest file size (when versions match)
- **Detailed Categorization**: Classifies duplicates as:
  - Same hash (identical files)
  - Different versions
  - Same version but different hash (e.g., trimmed variants)
- **TFM Detection**: Automatically infers Target Framework Monikers from assembly metadata
- **Size Reduction**: Optional removal of duplicates with archive re-creation
- **Multiple Formats**: Supports both .zip and .tar.gz archives

## Installation

### Prerequisites

- .NET 9.0 SDK or later

### Build from Source

```bash
git clone https://github.com/MichaelSimons/SdkLayoutAnalyzer.git
cd SdkLayoutAnalyzer
dotnet build
```

## Usage

### Basic Analysis

Analyze an SDK archive and output duplicate file information:

```bash
dotnet run <path-to-sdk-archive>
```

### Command Line Options

```
Usage: SdkLayoutAnalyzer <path-to-sdk-zip-or-tar> [subdirectory-relative-to-sdk-root] [--scan=assemblies|non-assemblies] [--remove-duplicates]

Arguments:
  path-to-sdk-archive              Path to .zip or .tar.gz SDK archive
  subdirectory-relative-to-sdk-root Optional subdirectory to analyze (default: "sdk")

Options:
  --scan=assemblies (default)      Scan .dll and .exe files only
  --scan=non-assemblies            Scan all files except .dll and .exe
  --remove-duplicates              Remove duplicate files and create deduplicated archive
```

### Examples

**Analyze assemblies in default SDK directory:**
```bash
dotnet run dotnet-sdk-10.0.100-linux-x64.tar.gz
```

**Analyze specific subdirectory:**
```bash
dotnet run dotnet-sdk-10.0.100-linux-x64.tar.gz sdk
```

**Analyze non-assembly files:**
```bash
dotnet run dotnet-sdk-10.0.100-linux-x64.tar.gz sdk --scan=non-assemblies
```

**Create deduplicated archive:**
```bash
dotnet run dotnet-sdk-10.0.100-linux-x64.tar.gz sdk --remove-duplicates
```

## Output Format

The tool outputs CSV data to stdout with the following columns:

### Duplicate File Listing

```
Filename,Culture,TargetFramework,RelativePath,AssemblyVersion,FileVersion,FileHash,FileSize
```

### Summary Statistics

After the file listing, a summary section provides:

- **Total duplicated files**: Count of extra copies that could be removed
- **Total duplicated file content (MB)**: Size that could be saved
- **Duplicate categorization**:
  - Duplicates with same hash as file to keep
  - Duplicates with different version
  - Duplicates with same version but different hash
- **Top 10 Largest Duplicated Files**: Sorted by aggregate duplicated size

### Archive Size Comparison (with --remove-duplicates)

When using `--remove-duplicates`, additional statistics show:
- Original archive size (MB)
- New archive size (MB)
- Size reduction (MB and %)

## Example Output

```
Filename,Culture,TargetFramework,RelativePath,AssemblyVersion,FileVersion,FileHash,FileSize
dotnet.dll,neutral,".NETCoreApp,Version=v10.0",sdk/10.0.100/dotnet.dll,10.0.100.0,10.1.25.52411,069d3866...,3915016
dotnet.dll,neutral,".NETCoreApp,Version=v10.0",sdk/10.0.100/DotnetTools/dotnet-watch/10.0.100/tools/net10.0/any/dotnet.dll,10.0.100.0,10.1.25.52411,2a86c7e6...,1711888

Total duplicated files (extra copies): 816
Total duplicated file content (MB): 140.2

Duplicate categorization (relative to lowest version file to keep):
  Duplicates with same hash as file to keep: 663
  Duplicates with different version: 40
  Duplicates with same version but different hash: 113

Top 10 Largest Duplicated Files:
Filename,Culture,TargetFramework,DuplicateCount,DuplicatedSize(MB)
Microsoft.CodeAnalysis.CSharp.dll,neutral,".NETCoreApp,Version=v9.0",3,24.7
Microsoft.CodeAnalysis.dll,neutral,".NETCoreApp,Version=v9.0",3,10.8
...
```

## How It Works

### TFM Detection

The tool detects Target Framework Monikers using multiple strategies:

1. **TargetFrameworkAttribute**: Reads the assembly's explicit TFM attribute
2. **Assembly References**: Infers TFM from referenced assemblies:
   - `netstandard` references → `.NETStandard,Version=vX.Y`
   - `System.Runtime` version → `.NETCoreApp,Version=vX.0` or `.NETFramework,Version=vX.Y`
   - `mscorlib` version → `.NETFramework,Version=vX.Y`

### Duplicate Selection Logic

When multiple copies of a file exist, the tool selects which to keep based on:

1. **Lowest version first**: Files with older versions are preferred (better compatibility)
2. **Largest file as tie-breaker**: When versions match, keep the largest file (likely most complete)

### File Removal (--remove-duplicates)

When creating a deduplicated archive:

1. Extracts the original archive to a temporary directory
2. Identifies all duplicate files
3. Removes all duplicates except the selected file to keep
4. Re-creates the archive in the same format (zip or tar.gz)
5. Names output as `<original-name>-deduplicated.<ext>`
6. Reports size savings

## Use Cases

- **SDK Size Analysis**: Understand duplication in .NET SDK distributions
- **Optimization Planning**: Identify largest opportunities for size reduction
- **Build Validation**: Detect unexpected duplication in SDK builds
- **Cross-Platform Comparison**: Compare duplication between Windows/Linux/macOS SDKs
- **Trend Analysis**: Track duplication changes across .NET versions

## Related Work

This tool was created to support the [Eliminate Duplicate SDK Files](https://github.com/dotnet/sdk/issues/41128) initiative for the .NET SDK.

## Contributing

Contributions are welcome! Please feel free to submit issues or pull requests.

## License

This project is licensed under the MIT License.
