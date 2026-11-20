@echo off
rem sfmodcheck was renamed sf-mod-check in 0.8.0. This file runs it under the old name; it will be dropped in a later release.
"%~dp0sf-mod-check.exe" %*
exit /b %ERRORLEVEL%
