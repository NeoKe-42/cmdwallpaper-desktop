@echo off
title Kill CMD Wallpaper Desktop
echo Killing CMD Wallpaper Desktop...
taskkill /F /IM "CMD Wallpaper Desktop.exe" 2>nul
taskkill /F /IM "cmdwallpaper_agent.exe" 2>nul
taskkill /F /IM "desktop_host.exe" 2>nul
taskkill /F /IM "electron.exe" 2>nul
echo Done.
pause
