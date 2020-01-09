#!/bin/sh
dotnet pack -c release -p:PackageVersion=$TRAVIS_TAG

dotnet nuget push src/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
dotnet nuget push extensions/Oryx.NewtonsoftJson/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
dotnet nuget push extensions/Oryx.Protobuf/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
dotnet nuget push extensions/Oryx.SystemTextJson/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
dotnet nuget push extensions/Oryx.ThothJsonNet/bin/Release/*.nupkg -s $NUGET_SOURCE -k $NUGET_API_KEY
