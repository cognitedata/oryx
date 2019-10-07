#!/bin/sh
dotnet pack -c release -p:PackageVersion=$1
dotnet nuget push src/bin/Release/*.nupkg -s $2 -k $3
