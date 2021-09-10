﻿namespace WhMgr.Commands.Discord
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using DSharpPlus;
    using DSharpPlus.CommandsNext;
    using DSharpPlus.CommandsNext.Attributes;
    using DSharpPlus.Entities;
    using Microsoft.EntityFrameworkCore;

    using WhMgr.Configuration;
    using WhMgr.Data;
    using WhMgr.Data.Factories;
    using WhMgr.Extensions;
    using WhMgr.Localization;

    // TODO: Simplified IV stats postings via command with arg `list`
    // TODO: Get total IV found for IV stats
    // TODO: Include forms with shiny/iv stats

    public class DailyStats : BaseCommandModule
    {
        private readonly ConfigHolder _config;

        public DailyStats(ConfigHolder config)
        {
            _config = config;
        }

        [
            Command("shiny-stats"),
            RequirePermissions(Permissions.KickMembers),
        ]
        public async Task GetShinyStatsAsync(CommandContext ctx)
        {
            var guildId = ctx.Guild?.Id ?? ctx.Client.Guilds.Keys.FirstOrDefault(x => _config.Instance.Servers.ContainsKey(x));

            if (!_config.Instance.Servers.ContainsKey(guildId))
            {
                await ctx.RespondEmbed(Translator.Instance.Translate("ERROR_NOT_IN_DISCORD_SERVER"), DiscordColor.Red);
                return;
            }

            var server = _config.Instance.Servers[guildId];
            if (!server.DailyStats.ShinyStats.Enabled)
                return;

            var statsChannel = await ctx.Client.GetChannelAsync(server.DailyStats.ShinyStats.ChannelId);
            if (statsChannel == null)
            {
                Console.WriteLine($"Failed to get channel id {server.DailyStats.ShinyStats.ChannelId} to post shiny stats.");
                await ctx.RespondEmbed(Translator.Instance.Translate("SHINY_STATS_INVALID_CHANNEL").FormatText(new { author = ctx.User.Username }), DiscordColor.Yellow);
                return;
            }

            if (server.DailyStats.ShinyStats.ClearMessages)
            {
                await ctx.Client.DeleteMessages(server.DailyStats.ShinyStats.ChannelId);
            }

            var stats = await GetShinyStats(_config.Instance.Database.Scanner.ToString());
            var sorted = stats.Keys.ToList();
            sorted.Sort();
            if (sorted.Count > 0)
            {
                await statsChannel.SendMessageAsync(Translator.Instance.Translate("SHINY_STATS_TITLE").FormatText(new { date = DateTime.Now.Subtract(TimeSpan.FromHours(24)).ToLongDateString() }));
                await statsChannel.SendMessageAsync(Translator.Instance.Translate("SHINY_STATS_NEWLINE"));
            }

            foreach (var pokemon in sorted)
            {
                if (pokemon == 0)
                    continue;

                if (!MasterFile.Instance.Pokedex.ContainsKey(pokemon))
                    continue;

                var pkmn = MasterFile.Instance.Pokedex[pokemon];
                var pkmnStats = stats[pokemon];
                var chance = pkmnStats.Shiny == 0 || pkmnStats.Total == 0 ? 0 : Convert.ToInt32(pkmnStats.Total / pkmnStats.Shiny);
                if (chance == 0)
                {
                    await statsChannel.SendMessageAsync(Translator.Instance.Translate("SHINY_STATS_MESSAGE").FormatText(new
                    {
                        pokemon = pkmn.Name,
                        id = pokemon,
                        shiny = pkmnStats.Shiny.ToString("N0"),
                        total = pkmnStats.Total.ToString("N0"),
                    }));
                }
                else
                {
                    await statsChannel.SendMessageAsync(Translator.Instance.Translate("SHINY_STATS_MESSAGE_WITH_RATIO").FormatText(new
                    {
                        pokemon = pkmn.Name,
                        id = pokemon,
                        shiny = pkmnStats.Shiny.ToString("N0"),
                        total = pkmnStats.Total.ToString("N0"),
                        chance,
                    }));
                }
                Thread.Sleep(500);
            }

            var total = stats[0];
            var totalRatio = total.Shiny == 0 || total.Total == 0 ? 0 : Convert.ToInt32(total.Total / total.Shiny);
            if (totalRatio == 0)
            {
                //await statsChannel.SendMessageAsync(Translator.Instance.Translate("SHINY_STATS_TOTAL_MESSAGE").FormatText(total.Shiny.ToString("N0"), total.Total.ToString("N0")));
                // Error, try again
                await GetShinyStatsAsync(ctx);
            }
            else
            {
                await statsChannel.SendMessageAsync(Translator.Instance.Translate("SHINY_STATS_TOTAL_MESSAGE_WITH_RATIO").FormatText(new
                {
                    shiny = total.Shiny.ToString("N0"),
                    total = total.Total.ToString("N0"),
                    chance = totalRatio,
                }));
            }
        }

        [
            Command("iv-stats"),
            RequirePermissions(Permissions.KickMembers),
        ]
        public async Task GetIVStatsAsync(CommandContext ctx)
        {
            var guildId = ctx.Guild?.Id ?? ctx.Client.Guilds.Keys.FirstOrDefault(x => _config.Instance.Servers.ContainsKey(x));

            if (!_config.Instance.Servers.ContainsKey(guildId))
            {
                await ctx.RespondEmbed(Translator.Instance.Translate("ERROR_NOT_IN_DISCORD_SERVER"), DiscordColor.Red);
                return;
            }

            var server = _config.Instance.Servers[guildId];
            if (!server.DailyStats.IVStats.Enabled)
                return;

            var statsChannel = await ctx.Client.GetChannelAsync(server.DailyStats.IVStats.ChannelId);
            if (statsChannel == null)
            {
                Console.WriteLine($"Failed to get channel id {server.DailyStats.IVStats.ChannelId} to post shiny stats.");
                await ctx.RespondEmbed(Translator.Instance.Translate("SHINY_STATS_INVALID_CHANNEL").FormatText(ctx.User.Username), DiscordColor.Yellow);
                return;
            }

            if (server.DailyStats.IVStats.ClearMessages)
            {
                await ctx.Client.DeleteMessages(server.DailyStats.IVStats.ChannelId);
            }

            var stats = GetIvStats(_config.Instance.Database.Scanner.ToString());

            var sb = new System.Text.StringBuilder();
            foreach (var (pokemonId, count) in stats)
            {
                var pkmn = MasterFile.GetPokemon(pokemonId, 0);
                sb.AppendLine($"- {pkmn.Name} (#{pokemonId}) {count:N0}");
            }

            var embed = new DiscordEmbedBuilder
            {
                Title = $"100% Pokemon Found (Last 24 Hours)",
                Description = sb.ToString(),
            };
            await ctx.RespondAsync(embed.Build());
        }

        internal static async Task<Dictionary<uint, ShinyPokemonStats>> GetShinyStats(string scannerConnectionString)
        {
            var list = new Dictionary<uint, ShinyPokemonStats>
            {
                { 0, new ShinyPokemonStats { PokemonId = 0 } }
            };
            try
            {
                using (var ctx = DbContextFactory.CreateMapContext(scannerConnectionString))
                {
                    ctx.Database.SetCommandTimeout(TimeSpan.FromSeconds(30)); // 30 seconds timeout
                    var yesterday = DateTime.Now.Subtract(TimeSpan.FromHours(24)).ToString("yyyy/MM/dd");
                    var pokemonShiny = (await ctx.PokemonStatsShiny.ToListAsync()).Where(x => x.Date.ToString("yyyy/MM/dd") == yesterday).ToList();
                    var pokemonIV = (await ctx.PokemonStatsIV.ToListAsync()).Where(x => x.Date.ToString("yyyy/MM/dd") == yesterday)?.ToDictionary(x => x.PokemonId);
                    for (var i = 0; i < pokemonShiny.Count; i++)
                    {
                        var curPkmn = pokemonShiny[i];
                        if (curPkmn.PokemonId > 0)
                        {
                            if (!list.ContainsKey(curPkmn.PokemonId))
                            {
                                list.Add(curPkmn.PokemonId, new ShinyPokemonStats { PokemonId = curPkmn.PokemonId });
                            }

                            list[curPkmn.PokemonId].PokemonId = curPkmn.PokemonId;
                            list[curPkmn.PokemonId].Shiny += Convert.ToInt32(curPkmn.Count);
                            list[curPkmn.PokemonId].Total += pokemonIV.ContainsKey(curPkmn.PokemonId) ? Convert.ToInt32(pokemonIV[curPkmn.PokemonId].Count) : 0;
                        }
                    }
                    list.Values.ToList().ForEach(x =>
                    {
                        list[0].Shiny += x.Shiny;
                        list[0].Total += x.Total;
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex}");
            }
            return list;
        }

        // TODO: Configurable IV value?
        internal static Dictionary<uint, int> GetIvStats(string scannerConnectionString)
        {
            try
            {
                using (var ctx = DbContextFactory.CreateMapContext(scannerConnectionString))
                {
                    ctx.Database.SetCommandTimeout(TimeSpan.FromSeconds(30)); // 30 seconds timeout
                    var now = DateTime.UtcNow;
                    var hoursAgo = TimeSpan.FromHours(24);
                    var yesterday = Convert.ToInt64(Math.Round(now.Subtract(hoursAgo).GetUnixTimestamp()));
                    // Checks within last 24 hours and 100% IV (or use statistics cache?)
                    var pokemon = ctx.Pokemon
                        .Where(x => x.Attack != null && x.Defense != null && x.Stamina != null
                            && x.DisappearTime > yesterday
                            && x.Attack == 15
                            && x.Defense == 15
                            && x.Stamina == 15
                          )
                        .AsEnumerable()
                        .GroupBy(x => x.Id, y => y.IV)
                        .Select(g => new { name = g.Key, count = g.Count() })
                        .ToDictionary(x => x.name, y => y.count);
                    return pokemon;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex}");
            }
            return null;
        }

        internal class ShinyPokemonStats
        {
            public uint PokemonId { get; set; }

            public long Shiny { get; set; }

            public long Total { get; set; }
        }

        internal class IvPokemonStats
        {
            public uint PokemonId { get; set; }

            public long Count { get; set; }

            public long Total { get; set; }
        }
    }
}