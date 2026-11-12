#!/bin/sh
# Builds and runs the test driver. Usage: ./run.sh <command> [options]   (see README.md)
here="$(cd "$(dirname "$0")" && pwd)"
exec dotnet run --project "$here/Driver/Driver.csproj" -c Release -- "$@"
