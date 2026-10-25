# GlassPlayer

A simple music player for Windows with a clean look inspired by Windows 11. Supports Spotify, SoundCloud and local music files.

## Features

- Windows 11 Acrylic styling
- Spotify support
- SoundCloud support
- Play local music files
- Simple and clean interface

## Getting started

### Download

You can find the source code here:

[GlassPlayer on GitHub](https://github.com/Jonzu00/GlassPlayer)

### Building from source

You'll need Windows and Visual Studio with the .NET desktop development tools installed.

1. Clone the repository or download the source code.
2. Open the project folder.
3. Open the `.sln` file in Visual Studio, if available.
4. Let Visual Studio restore the required NuGet packages.
5. Select `Release` as the build configuration.
6. Build the project.

You can find the compiled files in the project's output folder, usually under `bin/Release`.

## Spotify setup

If you want to use Spotify, you'll need your own Spotify Developer API credentials.

1. Go to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard).
2. Log in and create a new app.
3. Copy your Client ID.
4. Add it to the configuration used by GlassPlayer.

Make sure to follow the Spotify setup required by the project. Keep any private credentials secure and don't upload them to GitHub.

## Local music

You can also use GlassPlayer to listen to music stored on your computer, without setting up Spotify.

## License

GlassPlayer is licensed under the GNU General Public License v3.0. See [LICENSE](LICENSE) for more information.
