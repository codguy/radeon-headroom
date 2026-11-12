@echo off
rem Emergency reset: removes the tuning table so the GPU goes back to its factory settings, then restarts
rem the GPU. Works without opening the Headroom window. Windows asks for admin rights, and a message box
rem tells you the result.
start "" "%~dp0Headroom.exe" --reset
