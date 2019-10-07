#!/bin/sh
dotnet pack -c release -p:PackageVersion=$TRAVIS_TAG
dotnet nuget push src/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
