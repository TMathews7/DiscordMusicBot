using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.EventArgs;
using DSharpPlus.VoiceNext;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace DiscordMusicBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("[Main] Starting bot initialization...");
            var bot = new Bot();
            Console.WriteLine("[Main] Bot created, running bot...");
            await bot.RunAsync();
            Console.WriteLine("[Main] Bot run finished, exiting...");
        }
    }

    class Bot
    {
        private readonly DiscordClient _client;
        private readonly CommandsNextExtension _commands;
        private readonly VoiceNextExtension _voice;

        public Bot()
        {
            Console.WriteLine("[Bot Constructor] Creating DiscordClient...");

            _client = new DiscordClient(new DiscordConfiguration
            {
                Token = "YOUR_BOT_TOKEN_HERE", // **Replace with your actual bot token**
                TokenType = TokenType.Bot,
                MinimumLogLevel = LogLevel.Information,
                Intents = DiscordIntents.All
            });

            Console.WriteLine("[Bot Constructor] Subscribing to client events...");
            _client.Ready += OnClientReady;
            _client.GuildAvailable += OnGuildAvailable;
            _client.ClientErrored += OnClientError;
            _client.VoiceStateUpdated += OnVoiceStateUpdated; // Subscribe to VoiceStateUpdated event

            Console.WriteLine("[Bot Constructor] Setting CommandsNext configuration...");
            var commandsConfig = new CommandsNextConfiguration
            {
                StringPrefixes = new[] { "!" },
                EnableMentionPrefix = true,
                EnableDms = true,
                EnableDefaultHelp = false
            };

            Console.WriteLine("[Bot Constructor] Initializing CommandsNext and registering BotCommands...");
            _commands = _client.UseCommandsNext(commandsConfig);
            _commands.RegisterCommands<BotCommands>();

            Console.WriteLine("[Bot Constructor] Initializing VoiceNext...");
            _voice = _client.UseVoiceNext();
        }

        public async Task RunAsync()
        {
            Console.WriteLine("[RunAsync] Connecting client...");
            await _client.ConnectAsync();
            Console.WriteLine("[RunAsync] Client connected. Now waiting indefinitely...");
            await Task.Delay(-1);
        }

        private Task OnClientReady(DiscordClient sender, ReadyEventArgs e)
        {
            Console.WriteLine("[OnClientReady] Bot is ready!");
            return Task.CompletedTask;
        }

        private Task OnGuildAvailable(DiscordClient sender, GuildCreateEventArgs e)
        {
            Console.WriteLine($"[OnGuildAvailable] Connected to guild: {e.Guild.Name}");
            return Task.CompletedTask;
        }

        private Task OnClientError(DiscordClient sender, ClientErrorEventArgs e)
        {
            Console.WriteLine($"[OnClientError] Exception: {e.Exception.Message}");
            return Task.CompletedTask;
        }

        // **New Event Handler for Voice State Updates**
        private Task OnVoiceStateUpdated(DiscordClient sender, VoiceStateUpdateEventArgs e)
        {
            // Check if the bot was the one whose voice state was updated
            if (e.After.Channel == null && e.Before.Channel != null && e.Before.User.Id == sender.CurrentUser.Id)
            {
                Console.WriteLine("[OnVoiceStateUpdated] Bot was disconnected from a voice channel.");
                BotCommands.ClearQueueAndResetFlags();
                Console.WriteLine("[OnVoiceStateUpdated] Cleared music queue and reset playback flags.");
            }
            return Task.CompletedTask;
        }
    }

    public class BotCommands : BaseCommandModule
    {
        private static readonly object queueLock = new object();

        private static Queue<string> musicQueue = new Queue<string>();
        private static Dictionary<string, string> _titleCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static bool isPlaying = false;
        private static bool skipRequested = false;

        // Absolute paths to the executables
        private const string YtDlpPath = @"C:\DiscordBot\yt-dlp.exe";
        private const string FfmpegPath = @"C:\DiscordBot\ffmpeg\bin\ffmpeg.exe";
        private const string SaveDirectory = @"C:\DiscordBot\Downloads";

        [Command("ping")]
        public async Task PingCommand(CommandContext ctx)
        {
            Console.WriteLine("[PingCommand] Received !ping");
            await ctx.RespondAsync("Pong!");
            Console.WriteLine("[PingCommand] Responded with Pong!");
        }

        [Command("say")]
        public async Task SayCommand(CommandContext ctx, [RemainingText] string message)
        {
            Console.WriteLine($"[SayCommand] Received !say with message: {message}");
            await ctx.RespondAsync(message);
            Console.WriteLine("[SayCommand] Repeated the message back.");
        }

        [Command("join")]
        public async Task JoinVoice(CommandContext ctx)
        {
            Console.WriteLine("[JoinVoice] Received !join");
            var vnc = ctx.Client.GetVoiceNext();
            var vncConnection = vnc.GetConnection(ctx.Guild);

            if (vncConnection != null)
            {
                Console.WriteLine("[JoinVoice] Already in a voice channel, responding...");
                await ctx.RespondAsync("I'm already connected to a voice channel.");
                return;
            }

            var vc = ctx.Member?.VoiceState?.Channel;
            if (vc == null)
            {
                Console.WriteLine("[JoinVoice] No voice channel found for user, responding...");
                await ctx.RespondAsync("You must be in a voice channel first.");
                return;
            }

            Console.WriteLine($"[JoinVoice] Connecting to channel {vc.Name}...");
            await vnc.ConnectAsync(vc);
            Console.WriteLine($"[JoinVoice] Joined {vc.Name} successfully.");
            await ctx.RespondAsync($"Joined **{vc.Name}**.");
        }

        [Command("play")]
        public async Task PlayMusic(CommandContext ctx, [RemainingText] string url)
        {
            Console.WriteLine($"[PlayMusic] Received !play with URL: {url}");
            var vnc = ctx.Client.GetVoiceNext();
            var vncConnection = vnc.GetConnection(ctx.Guild);
            if (vncConnection == null)
            {
                Console.WriteLine("[PlayMusic] Bot not in a voice channel, responding...");
                await ctx.RespondAsync("I'm not in a voice channel. Use `!join` first.");
                return;
            }

            var msg = await ctx.RespondAsync("Processing your request...");

            if (url.Contains("?list="))
            {
                Console.WriteLine("[PlayMusic] URL detected as playlist with ?list=...");
                var links = await GetLinksViaCableAyraApi(url);
                Console.WriteLine($"[PlayMusic] Got {links.Count} links from playlist extraction.");
                if (links.Count == 0)
                {
                    Console.WriteLine("[PlayMusic] No videos found, updating message...");
                    await msg.ModifyAsync("No videos found in that playlist.");
                    return;
                }
                await PlayPlaylist(ctx, msg, links);
            }
            else if (url.Contains("&list="))
            {
                Console.WriteLine("[PlayMusic] URL has &list=, telling user to provide direct link...");
                await msg.ModifyAsync("Provide a direct link to the playlist");
                return;
            }
            else
            {
                Console.WriteLine("[PlayMusic] Single video enqueuing...");
                lock (queueLock)
                {
                    musicQueue.Enqueue(url);
                    Console.WriteLine($"[PlayMusic] Enqueued URL: {url}");
                }
                await msg.ModifyAsync($"Added to queue: <{url}>");

                if (!isPlaying)
                {
                    Console.WriteLine("[PlayMusic] Player is not currently playing, starting next in queue...");
                    await PlayNextInQueue(ctx, vncConnection, msg);
                }
            }
        }

        private async Task<List<string>> GetLinksViaCableAyraApi(string originalPlaylistUrl)
        {
            Console.WriteLine($"[GetLinksViaCableAyraApi] Fetching playlist from: {originalPlaylistUrl}");
            var endpoint = "https://cable.ayra.ch/ytdl/playlist.php?API=1&url="
                + Uri.EscapeDataString(originalPlaylistUrl);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                + "AppleWebKit/537.36 (KHTML, like Gecko) "
                + "Chrome/110.0.5481.100 Safari/537.36");

            try
            {
                var response = await http.GetStringAsync(endpoint);
                var lines = response
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                Console.WriteLine($"[GetLinksViaCableAyraApi] Extracted {lines.Count} lines from API response.");
                return lines;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetLinksViaCableAyraApi] Error fetching playlist: {ex.Message}");
                return new List<string>();
            }
        }

        public async Task PlayPlaylist(CommandContext ctx, DSharpPlus.Entities.DiscordMessage msg, List<string> links)
        {
            Console.WriteLine($"[PlayPlaylist] Enqueuing {links.Count} playlist items...");
            int count = 0;
            lock (queueLock)
            {
                foreach (var link in links)
                {
                    musicQueue.Enqueue(link);
                    Console.WriteLine($"[PlayPlaylist] Enqueued: {link}");
                    count++;
                }
            }

            await msg.ModifyAsync($"Added **{count}** items to the queue.");

            var vnc = ctx.Client.GetVoiceNext();
            var vncConnection = vnc.GetConnection(ctx.Guild);
            if (!isPlaying && vncConnection != null)
            {
                Console.WriteLine("[PlayPlaylist] Player is idle, starting next track...");
                await PlayNextInQueue(ctx, vncConnection, msg);
            }
        }

        private async Task<string> GetVideoTitle(string url)
        {
            Console.WriteLine($"[GetVideoTitle] Fetching title for: {url}");
            if (_titleCache.TryGetValue(url, out var cachedTitle))
            {
                Console.WriteLine("[GetVideoTitle] Title was cached, returning cached value.");
                return cachedTitle;
            }

            var psi = new ProcessStartInfo
            {
                FileName = YtDlpPath, // Absolute path to yt-dlp.exe
                Arguments = $"--get-title \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true, // Capture errors
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                {
                    Console.WriteLine("[GetVideoTitle] Could not start yt-dlp process, returning Unknown Title.");
                    return "Unknown Title";
                }

                // Read both output and error
                string output = await process.StandardOutput.ReadToEndAsync();
                string errors = await process.StandardError.ReadToEndAsync();

                bool exited = process.WaitForExit(15000); // Wait up to 15 seconds
                if (!exited)
                {
                    Console.WriteLine("[GetVideoTitle] yt-dlp process timed out, killing process.");
                    process.Kill(true);
                    return "Unknown Title";
                }

                var title = output.Trim();
                Console.WriteLine($"[GetVideoTitle] StandardError: {errors}");
                Console.WriteLine($"[GetVideoTitle] Extracted title: {title}");

                if (string.IsNullOrEmpty(title))
                    title = "Unknown Title";

                _titleCache[url] = title;
                return title;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetVideoTitle] Exception: {ex.Message}");
                return "Unknown Title";
            }
        }

        private async Task PlayNextInQueue(CommandContext ctx, VoiceNextConnection vncConnection, DSharpPlus.Entities.DiscordMessage statusMessage)
        {
            Console.WriteLine("[PlayNextInQueue] Attempting to dequeue next item...");
            string video;
            lock (queueLock)
            {
                if (musicQueue.Any())
                {
                    video = musicQueue.Dequeue();
                    Console.WriteLine($"[PlayNextInQueue] Dequeued: {video}");
                }
                else
                {
                    Console.WriteLine("[PlayNextInQueue] Queue is empty, stopping playback.");
                    isPlaying = false;
                    return;
                }
            }

            isPlaying = true;
            skipRequested = false;  // Reset flags

            string videoId = ExtractYouTubeVideoId(video);
            string outputFile = Path.Combine(SaveDirectory, $"{videoId}.wav");

            var videoTitle = await GetVideoTitle(video);
            Console.WriteLine($"[PlayNextInQueue] Next track: {videoTitle} ({video})");

            await statusMessage.ModifyAsync($"Preparing next track... <{video}>");

            if (!Directory.Exists(SaveDirectory))
            {
                Console.WriteLine($"[PlayNextInQueue] Directory doesn't exist, creating: {SaveDirectory}");
                Directory.CreateDirectory(SaveDirectory);
            }

            if (!File.Exists(outputFile))
            {
                Console.WriteLine("[PlayNextInQueue] File not found, downloading & converting...");
                await statusMessage.ModifyAsync($"Downloading & converting: <{video}>");

                var psiYtdlp = new ProcessStartInfo
                {
                    FileName = YtDlpPath, // Absolute path to yt-dlp.exe
                    Arguments = $"-f bestaudio -o - \"{video}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true, // Capture errors
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                try
                {
                    using var ytDlpProcess = Process.Start(psiYtdlp);
                    if (ytDlpProcess == null)
                    {
                        Console.WriteLine("[PlayNextInQueue] Failed to start yt-dlp.");
                        await statusMessage.ModifyAsync("Failed to start `yt-dlp`.");
                        await PlayNextInQueue(ctx, vncConnection, statusMessage);
                        return;
                    }

                    var psiFfmpeg = new ProcessStartInfo
                    {
                        FileName = FfmpegPath, // Absolute path to ffmpeg.exe
                        Arguments = $"-i pipe:0 -ac 2 -ar 48000 -f wav \"{outputFile}\"",
                        RedirectStandardInput = true,
                        RedirectStandardError = true, // Capture errors
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var ffmpegProcess = Process.Start(psiFfmpeg);
                    if (ffmpegProcess == null)
                    {
                        Console.WriteLine("[PlayNextInQueue] Failed to start ffmpeg.");
                        await statusMessage.ModifyAsync("Failed to start `ffmpeg`.");
                        await PlayNextInQueue(ctx, vncConnection, statusMessage);
                        return;
                    }

                    Console.WriteLine("[PlayNextInQueue] Streaming audio from yt-dlp to ffmpeg...");
                    await ytDlpProcess.StandardOutput.BaseStream.CopyToAsync(ffmpegProcess.StandardInput.BaseStream);
                    ffmpegProcess.StandardInput.Close();

                    // Capture ffmpeg errors
                    string ffmpegErrors = await ffmpegProcess.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrEmpty(ffmpegErrors))
                    {
                        Console.WriteLine($"[PlayNextInQueue] FFmpeg StandardError: {ffmpegErrors}");
                    }

                    bool ytExited = ytDlpProcess.WaitForExit(15000); // Wait up to 15 seconds
                    if (!ytExited)
                    {
                        Console.WriteLine("[PlayNextInQueue] yt-dlp process timed out, killing process.");
                        ytDlpProcess.Kill(true);
                    }

                    bool ffmpegExited = ffmpegProcess.WaitForExit(15000); // Wait up to 15 seconds
                    if (!ffmpegExited)
                    {
                        Console.WriteLine("[PlayNextInQueue] ffmpeg process timed out, killing process.");
                        ffmpegProcess.Kill(true);
                    }

                    Console.WriteLine("[PlayNextInQueue] Download & convert completed.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PlayNextInQueue] Exception during download & convert: {ex.Message}");
                    await statusMessage.ModifyAsync($"Error downloading or converting `{videoTitle}`: {ex.Message}");
                    await PlayNextInQueue(ctx, vncConnection, statusMessage);
                    return;
                }
            }
            else
            {
                Console.WriteLine("[PlayNextInQueue] Existing wav file found, skipping download.");
            }

            await statusMessage.ModifyAsync($"Now playing `{videoTitle}`...");

            try
            {
                Console.WriteLine("[PlayNextInQueue] Opening WAV file to stream...");
                await using var fileStream = File.OpenRead(outputFile);
                var transmit = vncConnection.GetTransmitSink();

                byte[] buffer = new byte[3840];
                int bytesRead;
                while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    if (skipRequested)
                    {
                        Console.WriteLine("[PlayNextInQueue] skipRequested detected, breaking out of loop...");
                        break;
                    }

                    await transmit.WriteAsync(buffer.AsMemory(0, bytesRead));
                }

                Console.WriteLine("[PlayNextInQueue] Finished streaming audio.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayNextInQueue] Exception during playback: {ex.Message}");
                await statusMessage.ModifyAsync($"Error playing `{videoTitle}`: {ex.Message}");
            }

            if (skipRequested)
            {
                Console.WriteLine("[PlayNextInQueue] skipRequested is true, resetting skip flag and loading next track...");
                skipRequested = false;
                await PlayNextInQueue(ctx, vncConnection, statusMessage);
                return;
            }

            Console.WriteLine("[PlayNextInQueue] Track ended naturally, loading next track if any...");
            await PlayNextInQueue(ctx, vncConnection, statusMessage);
        }

        [Command("queue")]
        public async Task ShowQueue(CommandContext ctx, int page = 1)
        {
            Console.WriteLine($"[ShowQueue] Received !queue {page}");
            var thinkingMsg = await ctx.RespondAsync("Retrieving queue items, please wait...");

            int pageSize = 5;
            int totalItems;
            lock (queueLock)
            {
                totalItems = musicQueue.Count;
                Console.WriteLine($"[ShowQueue] Queue count = {totalItems}");
            }

            if (totalItems == 0)
            {
                Console.WriteLine("[ShowQueue] Queue is empty, telling user...");
                await thinkingMsg.ModifyAsync("The queue is currently empty.");
                return;
            }

            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            Console.WriteLine($"[ShowQueue] totalPages = {totalPages}");
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            int startIndex = (page - 1) * pageSize;
            string[] itemsThisPage;
            lock (queueLock)
            {
                itemsThisPage = musicQueue
                    .Skip(startIndex)
                    .Take(pageSize)
                    .ToArray();
                Console.WriteLine($"[ShowQueue] Page {page}, startIndex={startIndex}, itemsThisPage={itemsThisPage.Length}");
            }

            var lines = new List<string>();
            for (int i = 0; i < itemsThisPage.Length; i++)
            {
                var url = itemsThisPage[i];
                var title = await GetVideoTitle(url);
                int itemNumber = startIndex + i + 1;
                lines.Add($"{itemNumber}. {title} - <{url}>");
            }

            Console.WriteLine("[ShowQueue] Sending final queue text to Discord...");
            await thinkingMsg.ModifyAsync(
                $"**Queue Page {page}/{totalPages}** (showing {itemsThisPage.Length} of {totalItems} total)\n```{string.Join("\n", lines)}```");
        }

        [Command("shuffle")]
        public async Task ShuffleQueue(CommandContext ctx)
        {
            Console.WriteLine("[ShuffleQueue] Received !shuffle");
            lock (queueLock)
            {
                if (musicQueue.Count < 2)
                {
                    Console.WriteLine("[ShuffleQueue] Not enough items to shuffle.");
                    goto NotEnough;
                }

                var items = musicQueue.ToList();
                musicQueue.Clear();
                Console.WriteLine($"[ShuffleQueue] Shuffling {items.Count} items...");

                var rnd = new Random();
                for (int i = items.Count - 1; i > 0; i--)
                {
                    int j = rnd.Next(0, i + 1);
                    (items[i], items[j]) = (items[j], items[i]);
                }
                foreach (var item in items)
                {
                    musicQueue.Enqueue(item);
                }
                Console.WriteLine("[ShuffleQueue] Shuffle complete.");
            }

            await ctx.RespondAsync("Queue has been shuffled.");
            return;

        NotEnough:
            await ctx.RespondAsync("Not enough items in the queue to shuffle.");
        }

        [Command("skip")]
        public async Task SkipMusic(CommandContext ctx)
        {
            Console.WriteLine("[SkipMusic] Received !skip");
            var vnc = ctx.Client.GetVoiceNext().GetConnection(ctx.Guild);
            if (vnc == null)
            {
                Console.WriteLine("[SkipMusic] Bot not in voice channel, responding...");
                await ctx.RespondAsync("I'm not connected to any voice channel.");
                return;
            }
            if (!isPlaying)
            {
                Console.WriteLine("[SkipMusic] Nothing is playing, responding...");
                await ctx.RespondAsync("No track is currently playing.");
                return;
            }
            Console.WriteLine("[SkipMusic] Setting skipRequested = true and waiting 300ms...");
            await ctx.RespondAsync("Skipping the current track...");

            skipRequested = true;
            await Task.Delay(300);

            if (!isPlaying)
            {
                Console.WriteLine("[SkipMusic] After delay, isPlaying=false, telling user no music left.");
                await ctx.RespondAsync("Skipped. Nothing else is playing.");
            }
        }

        [Command("help")]
        public async Task HelpCommand(CommandContext ctx)
        {
            Console.WriteLine("[HelpCommand] Received !help");
            await ctx.RespondAsync(@"Commands:
        ```
        1) !ping     - Writes Pong!
        2) !say      - Repeats your message
        3) !join     - Joins the voice channel you're in
        4) !play     - Plays a single URL or an entire playlist
        5) !queue    - Shows the current queue
        6) !shuffle  - Randomly shuffles the queue
        7) !skip     - Skips the current track
        8) !clear    - Clears the music queue
        ```");
        }

        // **Removed the !stop Command**

        // **New !clear Command to Clear the Music Queue**
        [Command("clear")]
        public async Task ClearQueue(CommandContext ctx)
        {
            Console.WriteLine("[ClearQueue] Received !clear");
            bool wasEmpty;

            lock (queueLock)
            {
                wasEmpty = musicQueue.Count == 0;
                if (!wasEmpty)
                {
                    musicQueue.Clear();
                    Console.WriteLine("[ClearQueue] Cleared the music queue.");
                }
            }

            if (wasEmpty)
            {
                Console.WriteLine("[ClearQueue] Queue is already empty.");
                await ctx.RespondAsync("The queue is already empty.");
            }
            else
            {
                Console.WriteLine("[ClearQueue] Queue has been cleared.");
                await ctx.RespondAsync("The queue has been cleared.");
            }
        }

        // **Public Static Method to Clear Queue and Reset Flags**
        public static void ClearQueueAndResetFlags()
        {
            Console.WriteLine("[ClearQueueAndResetFlags] Clearing music queue and resetting playback flags...");
            lock (queueLock)
            {
                musicQueue.Clear();
                Console.WriteLine("[ClearQueueAndResetFlags] Music queue cleared.");
            }
            isPlaying = false;
            skipRequested = false;
            Console.WriteLine("[ClearQueueAndResetFlags] Playback flags reset.");
        }

        private static string ExtractYouTubeVideoId(string url)
        {
            Console.WriteLine($"[ExtractYouTubeVideoId] Extracting video ID from: {url}");
            var match = Regex.Match(url, @"(?:youtube\.com\/.*v=|youtu\.be\/)([^&\n?#]+)");
            if (match.Success)
            {
                Console.WriteLine($"[ExtractYouTubeVideoId] Found ID: {match.Groups[1].Value}");
                return match.Groups[1].Value;
            }
            Console.WriteLine("[ExtractYouTubeVideoId] No valid ID found, returning empty string.");
            return string.Empty;
        }
    }
}
