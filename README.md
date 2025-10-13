[![NuGet](https://img.shields.io/nuget/v/Unhinged.svg)](https://www.nuget.org/packages/Unhinged/)

# Unhinged
Linux C# Ultra fast socket(epoll) server for benchmark purposes.

# How to build
Currently the project is targeting <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
Adapt to your linux distro and run: dotnet publish -f net9.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed
