@echo off
setlocal
set "DOTNET_CLI_HOME=%~dp0.dotnet_home"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_NOLOGO=1"
set "USERPROFILE=%~dp0.dotnet_home"
set "APPDATA=%~dp0.dotnet_home\AppData\Roaming"
set "LOCALAPPDATA=%~dp0.dotnet_home\AppData\Local"
set "NUGET_PACKAGES=%~dp0.nuget\packages"
set "NUGET_HTTP_CACHE_PATH=%~dp0.nuget\v3-cache"
set "NUGET_PLUGINS_CACHE_PATH=%~dp0.nuget\plugins-cache"
dotnet %*
exit /b %ERRORLEVEL%
