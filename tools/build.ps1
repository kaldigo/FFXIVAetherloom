param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
dotnet build Aetherloom/Aetherloom.csproj -c Release "-p:Version=$Version" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version" --nologo
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$dll = Join-Path $PWD 'Aetherloom/bin/Release/Aetherloom.dll'
if ([Reflection.AssemblyName]::GetAssemblyName($dll).Version -ne [version]$Version) { throw 'DLL version differs from plan' }
dotnet run --project tests/Imports -c Release
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet run --project tests/Ownership -c Release
exit $LASTEXITCODE
