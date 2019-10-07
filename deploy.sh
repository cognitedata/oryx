#!/bin/sh
dotnet pack -c release -p:PackageVersion=$0
dotnet nuget push src/bin/Release/*.nupkg -s $1 -k $2
