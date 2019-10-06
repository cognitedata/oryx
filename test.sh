#!/bin/sh
dotnet test test /p:Include="[Oryx]*" /p:CollectCoverage=true /p:CoverletOutputFormat=lcov /p:CoverletOutput='../coverage.lcov'
