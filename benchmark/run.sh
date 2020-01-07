#!/bin/bash
dotnet build -c Release
sudo dotnet run build -c Release --runtimes netcoreapp3.1