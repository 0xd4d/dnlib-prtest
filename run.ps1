# Builds and runs the test driver. Usage: .\run.ps1 <command> [options]   (see README.md)
dotnet run --project "$PSScriptRoot/Driver/Driver.csproj" -c Release -- @args
exit $LASTEXITCODE
