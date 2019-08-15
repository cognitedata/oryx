#!/bin/sh
dotnet pack -c release -p:PackageVersion=$1
dotnet nuget push src/bin/Release/*.nupkg -s https://cognite.jfrog.io/cognite/api/nuget/nuget-local
