﻿using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using CounterStrikeSharp.API.Core.Capabilities;
using System.Text;

namespace DiscordUtilities
{
    [MinimumApiVersion(202)]
    public partial class DiscordUtilities : BasePlugin, IPluginConfig<DUConfig>
    {
        public override string ModuleName => "Discord Utilities";
        public override string ModuleAuthor => "Nocky (SourceFactory.eu)";
        public override string ModuleVersion => "2.0.7";
        public void OnConfigParsed(DUConfig config)
        {
            Config = config;
        }
        public override void OnAllPluginsLoaded(bool hotReload)
        {
            _ = LoadDiscordBOT();
            if (!string.IsNullOrEmpty(Config.Database.Password) && !string.IsNullOrEmpty(Config.Database.Host) && !string.IsNullOrEmpty(Config.Database.DatabaseName) && !string.IsNullOrEmpty(Config.Database.User))
            {
                databaseData = new DatabaseConnection
                {
                    Server = Config.Database.Host,
                    Port = (uint)Config.Database.Port,
                    User = Config.Database.User,
                    Database = Config.Database.DatabaseName,
                    Password = Config.Database.Password,
                };
                _ = CreateDatabaseConnection();
            }
            else
            {
                Perform_SendConsoleMessage($"You need to setup Database credentials in config", ConsoleColor.Red);
                throw new Exception("Database connection information is missing!");
            }

            if (string.IsNullOrEmpty(Config.ServerID))
            {
                Perform_SendConsoleMessage($"Invalid Discord Server ID!", ConsoleColor.Red);
                throw new Exception("Invalid Discord Server ID");
            }

            int counter = 0;
            while (!IsBotConnected)
            {
                counter++;
                if (counter > 5)
                {
                    Perform_SendConsoleMessage($"Discord BOT failed to connect!", ConsoleColor.Red);
                    throw new Exception("Discord BOT failed to connect");
                }
                Perform_SendConsoleMessage($"Loading Discord BOT...", ConsoleColor.DarkYellow);
                Thread.Sleep(3000);
            }
        }
        public override void Load(bool hotReload)
        {
            var DUApi = new DiscordUtilities();
            Capabilities.RegisterPluginCapability(DiscordUtilitiesAPI, () => DUApi);

            CreateCustomCommands();
            if (Config.UseCustomVariables)
                LoadCustomConditions();

            _ = LoadMapImages();
            IsDbConnected = false;
            IsBotConnected = false;
            IsDebug = Config.Debug;
            ServerId = Config.ServerID;
            UseCustomVariables = Config.UseCustomVariables;
            DateFormat = Config.DateFormat;
            savedInteractions.Clear();

            serverData.ModuleDirectory = ModuleDirectory;
            serverData.IP = Config.ServerIP;

            Server.ExecuteCommand("sv_hibernate_when_empty false");
            bool mapStarted = false;
            RegisterListener<Listeners.OnMapStart>(mapName =>
            {
                if (!mapStarted)
                {
                    mapStarted = true;
                    Server.ExecuteCommand("sv_hibernate_when_empty false");
                    playerData.Clear();
                    AddTimer(3.0f, () =>
                    {
                        UpdateServerData();
                        serverData.MapName = mapName;
                        serverData.GameDirectory = Server.GameDirectory;
                        serverData.MaxPlayers = Server.MaxPlayers.ToString();
                        ServerDataLoaded();
                    });

                    AddTimer(60.0f, () =>
                    {
                        UpdateServerData();
                        foreach (var player in Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV && p.Connected == PlayerConnectedState.PlayerConnected && p.AuthorizedSteamID != null && playerData.ContainsKey(p.Slot)))
                        {
                            playerData[player.Slot].PlayedTime++;
                        }
                    }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
                }
            });
            RegisterListener<Listeners.OnMapEnd>(() => { mapStarted = false; });
        }

        private async Task LoadDiscordBOT()
        {
            try
            {
                BotClient = new DiscordSocketClient(new DiscordSocketConfig()
                {
                    AlwaysDownloadUsers = true,
                    UseInteractionSnowflakeDate = false,
                    GatewayIntents = GatewayIntents.MessageContent | GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMembers
                });

                BotCommands = new CommandService();
                BotServices = new ServiceCollection()
                    .AddSingleton(BotClient)
                    .AddSingleton(BotCommands)
                    .BuildServiceProvider();

                await BotClient.LoginAsync(TokenType.Bot, Config.Token);
                await BotClient.StartAsync();

                BotClient.Ready += ReadyAsync;

                await Task.Delay(-1);
            }
            catch (Exception ex)
            {
                Perform_SendConsoleMessage($"An error occurred while initializing the Discord BOT: '{ex.Message}'", ConsoleColor.Red);
            }
        }
        private async Task ReadyAsync()
        {
            Perform_SendConsoleMessage("Discord BOT has been connected!", ConsoleColor.Green);
            IsBotConnected = true;
            BotLoaded();

            if (string.IsNullOrEmpty(ServerId))
                Perform_SendConsoleMessage("You do not have a completed 'Server ID'!", ConsoleColor.Red);
            else
            {
                var guild = BotClient!.GetGuild(ulong.Parse(ServerId));
                if (guild == null)
                    Perform_SendConsoleMessage($"Guild with id '{ServerId}' was not found!", ConsoleColor.Red);
            }

            string ActivityFormat = ReplaceServerDataVariables(Config.BotStatus.ActivityFormat);
            //await BotClient!.SetGameAsync(ActivityFormat, null, (ActivityType)Config.BotStatus.ActivityType);

            await BotClient!.SetActivityAsync(new Game(ActivityFormat, (ActivityType)Config.BotStatus.ActivityType, ActivityProperties.None));
            await BotClient.SetStatusAsync((UserStatus)Config.BotStatus.Status);

            var linkCommand = new SlashCommandBuilder()
                .WithName(Config.Link.DiscordCommand.ToLower())
                .WithDescription(Config.Link.DiscordDescription)
                .AddOption(Config.Link.DiscordOptionName.ToLower(), ApplicationCommandOptionType.String, Config.Link.DiscordOptionDescription, isRequired: true);

            try
            {
                if (Config.Link.Enabled)
                {
                    if (IsDbConnected)
                    {
                        if (IsDebug)
                            Perform_SendConsoleMessage($"Link Slash Command has been successfully updated/created", ConsoleColor.Cyan);
                        await BotClient.CreateGlobalApplicationCommandAsync(linkCommand.Build());
                    }
                    else
                    {
                        Perform_SendConsoleMessage($"Link Slash Command was not created because you do not have a database connected", ConsoleColor.Red);
                        throw new Exception("Link Slash Command was not created because you do not have a database connected");
                    }
                }
            }
            catch (Exception ex)
            {
                Perform_SendConsoleMessage($"An error occurred while updating Link Slash Commands: '{ex.Message}'", ConsoleColor.Red);
                throw new Exception($"An error occurred while updating Link Slash Commands: {ex.Message}");
            }

            try
            {
                BotClient.SlashCommandExecuted += SlashCommandHandler;
                BotClient.MessageReceived += MessageReceivedHandler;
                BotClient.InteractionCreated += InteractionCreatedHandler;
            }
            catch (Exception ex)
            {
                Perform_SendConsoleMessage($"An error occurred while creating handlers: '{ex.Message}'", ConsoleColor.Red);
                throw new Exception($"An error occurred while creating handlers: {ex.Message}");
            }
        }
        private Task InteractionCreatedHandler(SocketInteraction interaction)
        {
            if ((DateTime.Now - LastInteractionTime).TotalSeconds > 60)
            {
                savedInteractions.Clear();
            }

            if (interaction is SocketMessageComponent MessageComponent)
            {
                Event_InteractionCreated(interaction, MessageComponent);
            }
            else if (interaction.Type == InteractionType.ModalSubmit)
            {
                Event_ModalSubmited(interaction);
            }
            return Task.CompletedTask;
        }
        private Task MessageReceivedHandler(SocketMessage message)
        {
            if (message.Author.IsBot || message.Author.IsWebhook)
                return Task.CompletedTask;

            Event_MessageReceived(message);
            return Task.CompletedTask;
        }

        private async Task SlashCommandHandler(SocketSlashCommand command)
        {
            if (command.CommandName == Config.Link.DiscordCommand.ToLower())
                await DiscordLink_CMD(command);
            else
                Event_SlashCommand(command);
        }

        public override void Unload(bool hotReload)
        {
            if (updateTimer != null)
                updateTimer.Kill();

            if (IsBotConnected && BotClient != null)
            {
                BotClient.SlashCommandExecuted -= SlashCommandHandler;
                BotClient.MessageReceived -= MessageReceivedHandler;
                BotClient.InteractionCreated -= InteractionCreatedHandler;
            }
        }
        
        public static void Perform_SendConsoleMessage(string text, ConsoleColor color)
        {
            string prefix = "[Discord Utilities] ";
            string suffix = text;

            switch (color)
            {
                case ConsoleColor.Cyan:
                    prefix = "[Discord Utilities] (DEBUG): ";
                    break;
                case ConsoleColor.Red:
                    prefix = "[Discord Utilities] (ERROR): ";
                    break;
            }

            Console.ForegroundColor = color;
            Console.Write(prefix);

            Console.ForegroundColor = ConsoleColor.White;
            bool isInQuotes = false;

            foreach (char c in suffix)
            {
                if (c == '\'')
                {
                    isInQuotes = !isInQuotes;
                    continue;
                }
                Console.ForegroundColor = isInQuotes ? ConsoleColor.Yellow : ConsoleColor.White;
                Console.Write(c);
            }

            Console.WriteLine();
            Console.ResetColor();
        }
    }
}
