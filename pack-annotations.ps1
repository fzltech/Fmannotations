$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet build "$ScriptDir\src\Fmverify.Annotations.Analyzers\Fmverify.Annotations.Analyzers.csproj" -c Release
dotnet pack "$ScriptDir\src\Fmverify.Annotations\Fmverify.Annotations.csproj" -c Release -o "$ScriptDir\.packages"
