#!/bin/sh
dotnet pack -c release -p:PackageVersion=$TRAVIS_TAG

dotnet nuget push src/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
dotnet nuget push extensions/Newtonsoft.Json/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
dotnet nuget push extensions/Protobuf/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
dotnet nuget push extensions/System.Text.Json/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
dotnet nuget push extensions/Thoth.Json.Net/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
