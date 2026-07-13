# JellyNomad

JellyNomad serves to provide a classic TV-like experience fully contained within a Jellyfin instance. It includes the ability to seamlessly transition between randomly chosen media files and the option to show commercial or trailer breaks at set intervals. 

## Requirements

- Jellyfin 10.9 or later
- .NET 9 runtime

## Building

The project references Jellyfin DLLs directly. Before building, update the `<HintPath>` entries in `JellyNomad.csproj` to match your Jellyfin installation directory if it differs from `C:\Program Files\Jellyfin\Server\`.

```
dotnet build -c Release
```

The output DLL will be in `bin/Release/net9.0/`.

## Configuration

Open the plugin settings page at **Dashboard → Plugins → Nomad**.

### Global settings

| Setting | Description |
|---|---|
| Commercials Path | Absolute path to a folder of video files used as commercial breaks. This is distinct to allow for the commercials to exist outside of the Jellyfin library (or inside it and ignored). Supported formats: `.mp4`, `.mkv`, `.avi`, `.mov`, `.wmv`, `.mpg`, `.mpeg`, `.m4v`, `.ts`, `.m2ts`. |

### Channel settings

Add one or more channels. Each channel streams independently.

| Setting | Description |
|---|---|
| Name | The name of the channel. |
| Channel Image | Optional image for the channel tile. Accepts a URL or a file upload (stored as a data URL). |
| Selected Series | TV series from your library to include in this channel's rotation. |
| Hours Between Repeat | Minimum hours before an episode from the same series can play again. Set to `0` to disable. |
| Successive Episodes Allowed | How many episodes of the same series can play back-to-back before switching to a different series. Set to `0` to disable.|
| Allow Commercials | Play clips from the Commercials Path between episodes. Specific commericals may only play once per day.|
| Allow Trailers | Play trailers from your Jellyfin library between episodes. Specific trailers may only play once per day.|
| Max Trailer Rating | Highest MPAA rating to allow for trailers (`G`, `PG`, `PG-13`, `R`, `NC-17`). Trailers with no rating are treated as rated `R`. |
| Episodes Per Break | Number of episodes to play before inserting a break. |
| Break Item Count | Number of commercials/trailers to play per break. |

## Usage

After configuring at least one channel, open **Nomad** in the Jellyfin sidebar or home menu. Each channel should appear as a media item that can be played*. Once started, the channel will start picking random episodes from the selected series. This will loop infinitely, until the channel has been closed.

If any commercials or trailers have been enabled, those will play after every _Episodes Per Break_ number of episodes (See Settings Above). Breaks are configured so that unique trailers/commercials may only play a max of once per day. If all candidates have already played for a given day, episode playback will continue with no breaks.  

_*Note: Playback must be done from the list view. Playing from the details view will cause Jellyfin to not be able to recognize any available media, due to how the stream is configured.__

## Disclaimer

Some parts of this codebase were generated with AI. 

