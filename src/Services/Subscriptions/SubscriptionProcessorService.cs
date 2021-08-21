﻿namespace WhMgr.Services.Subscriptions
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using DSharpPlus;
    using DSharpPlus.Entities;
    //using Microsoft.Extensions.Logging;
    using InvasionCharacter = POGOProtos.Rpc.EnumWrapper.Types.InvasionCharacter;

    using WhMgr.Common;
    using WhMgr.Configuration;
    using WhMgr.Data;
    using WhMgr.Extensions;
    using WhMgr.Localization;
    using WhMgr.Queues;
    using WhMgr.Services.Alarms;
    using WhMgr.Services.Alarms.Filters;
    using WhMgr.Services.Cache;
    using WhMgr.Services.Discord;
    using WhMgr.Services.Geofence;
    using WhMgr.Services.Subscriptions.Models;
    using WhMgr.Services.Webhook.Models;

    public class SubscriptionProcessorService : ISubscriptionProcessorService
    {
        private readonly Microsoft.Extensions.Logging.ILogger<ISubscriptionProcessorService> _logger;
        private readonly ISubscriptionManagerService _subscriptionManager;
        private readonly ConfigHolder _config;
        private readonly IDiscordClientService _discordService;
        private readonly ISubscriptionProcessorQueueService _queue;
        private readonly IMapDataCache _mapDataCache;
        private readonly IStaticsticsService _statsService;

        public SubscriptionProcessorService(
            Microsoft.Extensions.Logging.ILogger<ISubscriptionProcessorService> logger,
            ISubscriptionManagerService subscriptionManager,
            ConfigHolder config,
            IDiscordClientService discordService,
            ISubscriptionProcessorQueueService queue,
            IMapDataCache mapDataCache,
            IStaticsticsService statsService)
        {
            _logger = logger;
            _subscriptionManager = subscriptionManager;
            _config = config;
            _discordService = discordService;
            _queue = queue;
            _mapDataCache = mapDataCache;
            _statsService = statsService;
        }

        public async Task ProcessPokemonSubscription(PokemonData pokemon)
        {
            var subUser = await _subscriptionManager.GetUserSubscriptionsAsync(342025055510855680, 266771160253988875);
            if (!MasterFile.Instance.Pokedex.ContainsKey(pokemon.Id))
                return;

            // Cache the result per-guild so that geospatial stuff isn't queried for every single subscription below
            var locationCache = new Dictionary<ulong, Geofence>();
            var pkmnCoord = new Coordinate(pokemon.Latitude, pokemon.Longitude);
            Geofence GetGeofence(ulong guildId)
            {
                if (!locationCache.TryGetValue(guildId, out var geofence))
                {
                    var geofences = _config.Instance.Servers[guildId].Geofences;
                    geofence = GeofenceService.GetGeofence(geofences, pkmnCoord);
                    locationCache.Add(guildId, geofence);
                }
                return geofence;
            }

            var subscriptions = _subscriptionManager.GetSubscriptionsByPokemonId(pokemon.Id);
            if (subscriptions == null)
            {
                _logger.LogWarning($"Failed to get subscriptions from database table.");
                return;
            }

            Subscription user;
            DiscordMember member = null;
            var pkmn = MasterFile.GetPokemon(pokemon.Id, pokemon.FormId);
            var matchesIV = false;
            var matchesLvl = false;
            var matchesGender = false;
            var matchesIVList = false;
            for (var i = 0; i < subscriptions.Count; i++)
            {
                //var start = DateTime.Now;
                try
                {
                    user = subscriptions[i];

                    if (!_config.Instance.Servers.ContainsKey(user.GuildId))
                        continue;

                    if (!_config.Instance.Servers[user.GuildId].Subscriptions.Enabled)
                        continue;

                    if (!_discordService.DiscordClients.ContainsKey(user.GuildId))
                        continue;

                    var client = _discordService.DiscordClients[user.GuildId];

                    try
                    {
                        member = await client.GetMemberById(user.GuildId, user.UserId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"FAILED TO GET MEMBER BY ID {user.UserId}");
                        _logger.LogError(ex.ToString());
                        continue;
                    }

                    if (member?.Roles == null)
                        continue;

                    if (!member.HasSupporterRole(_config.Instance.Servers[user.GuildId].DonorRoleIds.Keys.ToList()))
                    {
                        _logger.LogDebug($"User {member?.Username} ({user.UserId}) is not a supporter, skipping pokemon {pkmn.Name}...");
                        // Automatically disable users subscriptions if not supporter to prevent issues
                        //user.Enabled = false;
                        //user.Save(false);
                        continue;
                    }

                    // Check donor role access for Pokemon
                    if (!member.HasRoleAccess(_config.Instance.Servers[user.GuildId].DonorRoleIds, SubscriptionAccessType.Pokemon))
                        continue;

                    var form = Translator.Instance.GetFormName(pokemon.FormId);
                    var pokemonSubscriptions = user.Pokemon.Where(x =>
                        x.PokemonId.Contains(pokemon.Id) && (x.Forms?.Contains(form) ?? true)
                    );
                    foreach (var pkmnSub in pokemonSubscriptions)
                    {
                        matchesIV = Filters.MatchesIV(pokemon.IV, (uint)pkmnSub.MinimumIV, 100);
                        //var matchesCP = _whm.Filters.MatchesCpFilter(pkmn.CP, subscribedPokemon.MinimumCP);
                        matchesLvl = Filters.MatchesLvl(pokemon.Level, (uint)pkmnSub.MinimumLevel, (uint)pkmnSub.MaximumLevel);
                        matchesGender = Filters.MatchesGender(pokemon.Gender, pkmnSub.Gender);
                        matchesIVList = pkmnSub.IVList?.Contains($"{pokemon.Attack}/{pokemon.Defense}/{pokemon.Stamina}") ?? false;

                        if (!(
                            (!pkmnSub.HasStats && matchesIV && matchesLvl && matchesGender) ||
                            (pkmnSub.HasStats && matchesIVList)
                            ))
                            continue;

                        /*
                        TODO: if (!(float.TryParse(pokemon.Height, out var height) && float.TryParse(pokemon.Weight, out var weight) && Filters.MatchesSize(pokemon.Id.GetSize(height, weight), pkmnSub.Size)))
                        {
                            // Pokemon doesn't match size
                            continue;
                        }
                        */

                        var geofence = GetGeofence(user.GuildId);
                        if (geofence == null)
                        {
                            //_logger.LogWarning($"Failed to lookup city from coordinates {pkmn.Latitude},{pkmn.Longitude} {db.Pokemon[pkmn.Id].Name} {pkmn.IV}, skipping...");
                            continue;
                        }

                        var globalLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, user.Location, true) == 0);
                        var subscriptionLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, pkmnSub.Location, true) == 0);
                        var globalDistanceMatches = globalLocation?.DistanceM > 0 && globalLocation?.DistanceM > new Coordinate(globalLocation?.Latitude ?? 0, globalLocation?.Longitude ?? 0).DistanceTo(pkmnCoord);
                        var subscriptionDistanceMatches = subscriptionLocation?.DistanceM > 0 && subscriptionLocation?.DistanceM > new Coordinate(subscriptionLocation?.Latitude ?? 0, subscriptionLocation?.Longitude ?? 0).DistanceTo(pkmnCoord);
                        var geofenceMatches = pkmnSub.Areas.Select(x => x.ToLower()).Contains(geofence.Name.ToLower());

                        // If set distance does not match and no geofences match, then skip Pokemon...
                        if (!globalDistanceMatches && !subscriptionDistanceMatches && !geofenceMatches)
                            continue;

                        var embed = pokemon.GenerateEmbedMessage(new AlarmMessageSettings
                        {
                            GuildId = user.GuildId,
                            Client = client,
                            Config = _config,
                            Alarm = null,
                            City = geofence.Name,
                            MapDataCache = _mapDataCache,
                        });

                        //var end = DateTime.Now.Subtract(start);
                        //_logger.LogDebug($"Took {end} to process Pokemon subscription for user {user.UserId}");
                        embed.Embeds.ForEach(x => EnqueueEmbed(new NotificationItem
                        {
                            Subscription = user,
                            Member = member,
                            Embed = x.GenerateDiscordMessage(),
                            Description = pkmn.Name,
                            City = geofence.Name,
                            Pokemon = pokemon,
                        }));

                        _statsService.TotalPokemonSubscriptionsSent++;
                        Thread.Sleep(5);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.ToString());
                }
            }

            subscriptions.Clear();
            subscriptions = null;
            member = null;
            user = null;
            pokemon = null;

            await Task.CompletedTask;
        }

        public async Task ProcessPvpSubscription(PokemonData pokemon)
        {
            if (!MasterFile.Instance.Pokedex.ContainsKey(pokemon.Id))
                return;

            // Cache the result per-guild so that geospatial stuff isn't queried for every single subscription below
            var locationCache = new Dictionary<ulong, Geofence>();
            var pkmnCoord = new Coordinate(pokemon.Latitude, pokemon.Longitude);
            Geofence GetGeofence(ulong guildId)
            {
                if (!locationCache.TryGetValue(guildId, out var geofence))
                {
                    var geofences = _config.Instance.Servers[guildId].Geofences;
                    geofence = GeofenceService.GetGeofence(geofences, pkmnCoord);
                    locationCache.Add(guildId, geofence);
                }
                return geofence;
            }

            // TODO: PvP subscriptions support for evolutions not just base evo
            var subscriptions = _subscriptionManager.GetSubscriptionsByPvpPokemonId(pokemon.Id);
            if (subscriptions == null)
            {
                _logger.LogWarning($"Failed to get subscriptions from database table.");
                return;
            }

            Subscription user;
            DiscordMember member = null;
            var pkmn = MasterFile.GetPokemon(pokemon.Id, pokemon.FormId);
            var matchesGreat = false;
            var matchesUltra = false;
            for (var i = 0; i < subscriptions.Count; i++)
            {
                //var start = DateTime.Now;
                try
                {
                    user = subscriptions[i];

                    if (!_config.Instance.Servers.ContainsKey(user.GuildId))
                        continue;

                    if (!_config.Instance.Servers[user.GuildId].Subscriptions.Enabled)
                        continue;

                    if (!_discordService.DiscordClients.ContainsKey(user.GuildId))
                        continue;

                    var client = _discordService.DiscordClients[user.GuildId];

                    try
                    {
                        member = await client.GetMemberById(user.GuildId, user.UserId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"FAILED TO GET MEMBER BY ID {user.UserId}");
                        _logger.LogError($"Error: {ex}");
                        continue;
                    }

                    if (member?.Roles == null)
                        continue;

                    if (!member.HasSupporterRole(_config.Instance.Servers[user.GuildId].DonorRoleIds.Keys.ToList()))
                    {
                        _logger.LogDebug($"User {member?.Username} ({user.UserId}) is not a supporter, skipping pvp pokemon {pkmn.Name}...");
                        // Automatically disable users subscriptions if not supporter to prevent issues
                        //user.Enabled = false;
                        //user.Save(false);
                        continue;
                    }

                    // Check donor role access for PvP
                    if (!member.HasRoleAccess(_config.Instance.Servers[user.GuildId].DonorRoleIds, SubscriptionAccessType.PvP))
                        continue;

                    var form = Translator.Instance.GetFormName(pokemon.FormId);
                    var pokemonSubscriptions = user.PvP.Where(x =>
                        x.PokemonId.Contains(pokemon.Id) && (x.Forms?.Contains(form) ?? true)
                    );
                    foreach (var pkmnSub in pokemonSubscriptions)
                    {
                        matchesGreat = pokemon.GreatLeague != null && (pokemon.GreatLeague?.Exists(x => pkmnSub.League == PvpLeague.Great &&
                                                                         (x.CP ?? 0) >= Strings.MinimumGreatLeagueCP && (x.CP ?? 0) <= Strings.MaximumGreatLeagueCP &&
                                                                         (x.Rank ?? 4096) <= pkmnSub.MinimumRank &&
                                                                         (x.Percentage ?? 0) * 100 >= pkmnSub.MinimumPercent) ?? false);
                        matchesUltra = pokemon.UltraLeague != null && (pokemon.UltraLeague?.Exists(x => pkmnSub.League == PvpLeague.Ultra &&
                                                                         (x.CP ?? 0) >= Strings.MinimumUltraLeagueCP && (x.CP ?? 0) <= Strings.MaximumUltraLeagueCP &&
                                                                         (x.Rank ?? 4096) <= pkmnSub.MinimumRank &&
                                                                         (x.Percentage ?? 0) * 100 >= pkmnSub.MinimumPercent) ?? false);

                        // Check if Pokemon IV stats match any relevant great or ultra league ranks, if not skip.
                        if (!matchesGreat && !matchesUltra)
                            continue;

                        var geofence = GetGeofence(user.GuildId);
                        if (geofence == null)
                        {
                            //_logger.Warn($"Failed to lookup city from coordinates {pkmn.Latitude},{pkmn.Longitude} {db.Pokemon[pkmn.Id].Name} {pkmn.IV}, skipping...");
                            continue;
                        }

                        var globalLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, user.Location, true) == 0);
                        var subscriptionLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, pkmnSub.Location, true) == 0);
                        var globalDistanceMatches = globalLocation?.DistanceM > 0 && globalLocation?.DistanceM > new Coordinate(globalLocation?.Latitude ?? 0, globalLocation?.Longitude ?? 0).DistanceTo(pkmnCoord);
                        var subscriptionDistanceMatches = subscriptionLocation?.DistanceM > 0 && subscriptionLocation?.DistanceM > new Coordinate(subscriptionLocation?.Latitude ?? 0, subscriptionLocation?.Longitude ?? 0).DistanceTo(pkmnCoord);
                        var geofenceMatches = pkmnSub.Areas.Select(x => x.ToLower()).Contains(geofence.Name.ToLower());

                        // If set distance does not match and no geofences match, then skip Pokemon...
                        if (!globalDistanceMatches && !subscriptionDistanceMatches && !geofenceMatches)
                            continue;

                        var embed = pokemon.GenerateEmbedMessage(new AlarmMessageSettings
                        {
                            GuildId = user.GuildId,
                            Client = client,
                            Config = _config,
                            Alarm = null,
                            City = geofence.Name,
                        });
                        //var end = DateTime.Now.Subtract(start);
                        //_logger.Debug($"Took {end} to process PvP subscription for user {user.UserId}");
                        embed.Embeds.ForEach(x => EnqueueEmbed(new NotificationItem
                        {
                            Subscription = user,
                            Member = member,
                            Embed = x.GenerateDiscordMessage(),
                            Description = pkmn.Name,
                            City = geofence.Name,
                        }));

                        _statsService.TotalPvpSubscriptionsSent++;
                        Thread.Sleep(5);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error: {ex}");
                }
            }

            subscriptions.Clear();
            subscriptions = null;
            member = null;
            user = null;
            pokemon = null;

            await Task.CompletedTask;
        }

        public async Task ProcessRaidSubscription(RaidData raid)
        {
            if (!MasterFile.Instance.Pokedex.ContainsKey(raid.PokemonId))
                return;

            // Cache the result per-guild so that geospatial stuff isn't queried for every single subscription below
            var locationCache = new Dictionary<ulong, Geofence>();
            var raidCoord = new Coordinate(raid.Latitude, raid.Longitude);
            Geofence GetGeofence(ulong guildId)
            {
                if (!locationCache.TryGetValue(guildId, out var geofence))
                {
                    var geofences = _config.Instance.Servers[guildId].Geofences;
                    geofence = GeofenceService.GetGeofence(geofences, raidCoord);
                    locationCache.Add(guildId, geofence);
                }
                return geofence;
            }

            var subscriptions = _subscriptionManager.GetSubscriptionsByRaidPokemonId(raid.PokemonId);
            if (subscriptions == null)
            {
                _logger.LogWarning($"Failed to get subscriptions from database table.");
                return;
            }

            Subscription user;
            var pokemon = MasterFile.GetPokemon(raid.PokemonId, raid.Form);
            for (int i = 0; i < subscriptions.Count; i++)
            {
                //var start = DateTime.Now;
                try
                {
                    user = subscriptions[i];

                    if (!_config.Instance.Servers.ContainsKey(user.GuildId))
                        continue;

                    if (!_config.Instance.Servers[user.GuildId].Subscriptions.Enabled)
                        continue;

                    if (!_discordService.DiscordClients.ContainsKey(user.GuildId))
                        continue;

                    var client = _discordService.DiscordClients[user.GuildId];

                    var member = await client.GetMemberById(user.GuildId, user.UserId);
                    if (member == null)
                    {
                        _logger.LogWarning($"Failed to find member with id {user.UserId}.");
                        continue;
                    }

                    if (!member.HasSupporterRole(_config.Instance.Servers[user.GuildId].DonorRoleIds.Keys.ToList()))
                    {
                        _logger.LogInformation($"User {user.UserId} is not a supporter, skipping raid boss {pokemon.Name}...");
                        // Automatically disable users subscriptions if not supporter to prevent issues
                        //user.Enabled = false;
                        //user.Save(false);
                        continue;
                    }

                    // Check donor role access for Raids
                    if (!member.HasRoleAccess(_config.Instance.Servers[user.GuildId].DonorRoleIds, SubscriptionAccessType.Raids))
                        continue;

                    var form = Translator.Instance.GetFormName(raid.Form);
                    var subPkmn = user.Raids.FirstOrDefault(x =>
                        x.PokemonId.Contains(raid.PokemonId) && (x.Forms?.Contains(form) ?? true)
                    );
                    // Not subscribed to Pokemon
                    if (subPkmn == null)
                    {
                        //_logger.Debug($"Skipping notification for user {member.DisplayName} ({member.Id}) for raid boss {pokemon.Name}, raid is in city '{loc.Name}'.");
                        continue;
                    }

                    var geofence = GetGeofence(user.GuildId);
                    if (geofence == null)
                    {
                        //_logger.Warn($"Failed to lookup city from coordinates {pkmn.Latitude},{pkmn.Longitude} {db.Pokemon[pkmn.Id].Name} {pkmn.IV}, skipping...");
                        continue;
                    }

                    if (!raid.IsExEligible && subPkmn.IsExEligible)
                    {
                        // Skip raids that are not ex eligible when we want ex eligible raids
                        continue;
                    }

                    var globalLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, user.Location, true) == 0);
                    var subscriptionLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, subPkmn.Location, true) == 0);
                    var globalDistanceMatches = globalLocation?.DistanceM > 0 && globalLocation?.DistanceM > new Coordinate(globalLocation?.Latitude ?? 0, globalLocation?.Longitude ?? 0).DistanceTo(raidCoord);
                    var subscriptionDistanceMatches = subscriptionLocation?.DistanceM > 0 && subscriptionLocation?.DistanceM > new Coordinate(subscriptionLocation?.Latitude ?? 0, subscriptionLocation?.Longitude ?? 0).DistanceTo(raidCoord);
                    var geofenceMatches = subPkmn.Areas.Select(x => x.ToLower()).Contains(geofence.Name.ToLower());

                    // If set distance does not match and no geofences match, then skip Raid Pokemon...
                    if (!globalDistanceMatches && !subscriptionDistanceMatches && !geofenceMatches)
                        continue;

                    var embed = raid.GenerateEmbedMessage(new AlarmMessageSettings
                    {
                        GuildId = user.GuildId,
                        Client = client,
                        Config = _config,
                        Alarm = null,
                        City = geofence.Name
                    });
                    //var end = DateTime.Now;
                    //_logger.Debug($"Took {end} to process raid subscription for user {user.UserId}");
                    embed.Embeds.ForEach(x => EnqueueEmbed(new NotificationItem
                    {
                        Subscription = user,
                        Member = member,
                        Embed = x.GenerateDiscordMessage(),
                        Description = pokemon.Name,
                        City = geofence.Name
                    }));

                    // TODO: _statsService.SubscriptionRaidsSent++;
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error: {ex}");
                }
            }

            subscriptions.Clear();
            subscriptions = null;
            user = null;

            await Task.CompletedTask;
        }

        public async Task ProcessQuestSubscription(QuestData quest)
        {
            var reward = quest.Rewards.FirstOrDefault().Info;
            var rewardKeyword = quest.GetReward();
            var questName = quest.GetQuestMessage();

            // Cache the result per-guild so that geospatial stuff isn't queried for every single subscription below
            var locationCache = new Dictionary<ulong, Geofence>();
            var questCoord = new Coordinate(quest.Latitude, quest.Longitude);
            Geofence GetGeofence(ulong guildId)
            {
                if (!locationCache.TryGetValue(guildId, out var geofence))
                {
                    var geofences = _config.Instance.Servers[guildId].Geofences;
                    geofence = GeofenceService.GetGeofence(geofences, questCoord);
                    locationCache.Add(guildId, geofence);
                }
                return geofence;
            }

            var subscriptions = _subscriptionManager.GetSubscriptionsByQuest(quest.PokestopName, rewardKeyword);
            if (subscriptions == null)
            {
                _logger.LogWarning($"Failed to get subscriptions from database table.");
                return;
            }

            bool isSupporter;
            Subscription user;
            for (int i = 0; i < subscriptions.Count; i++)
            {
                //var start = DateTime.Now;
                try
                {
                    user = subscriptions[i];

                    if (!_config.Instance.Servers.ContainsKey(user.GuildId))
                        continue;

                    if (!_config.Instance.Servers[user.GuildId].Subscriptions.Enabled)
                        continue;

                    if (!_discordService.DiscordClients.ContainsKey(user.GuildId))
                        continue;

                    var client = _discordService.DiscordClients[user.GuildId];

                    var member = await client.GetMemberById(user.GuildId, user.UserId);
                    if (member == null)
                    {
                        _logger.LogWarning($"Failed to find member with id {user.UserId}.");
                        continue;
                    }

                    isSupporter = member.HasSupporterRole(_config.Instance.Servers[user.GuildId].DonorRoleIds.Keys.ToList());
                    if (!isSupporter)
                    {
                        _logger.LogInformation($"User {user.UserId} is not a supporter, skipping quest {questName}...");
                        // Automatically disable users subscriptions if not supporter to prevent issues
                        //user.Enabled = false;
                        //user.Save(false);
                        continue;
                    }

                    // Check donor role access for Quests
                    if (!member.HasRoleAccess(_config.Instance.Servers[user.GuildId].DonorRoleIds, SubscriptionAccessType.Quests))
                        continue;

                    var subQuest = user.Quests.FirstOrDefault(x => rewardKeyword.ToLower().Contains(x.RewardKeyword.ToLower()));
                    // Not subscribed to quest
                    if (subQuest == null)
                    {
                        //_logger.Debug($"Skipping notification for user {member.DisplayName} ({member.Id}) for quest {questName} because the quest is in city '{loc.Name}'.");
                        continue;
                    }

                    var geofence = GetGeofence(user.GuildId);
                    if (geofence == null)
                    {
                        //_logger.Warn($"Failed to lookup city from coordinates {pkmn.Latitude},{pkmn.Longitude} {db.Pokemon[pkmn.Id].Name} {pkmn.IV}, skipping...");
                        continue;
                    }

                    var globalLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, user.Location, true) == 0);
                    var subscriptionLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, subQuest.Location, true) == 0);
                    var globalDistanceMatches = globalLocation?.DistanceM > 0 && globalLocation?.DistanceM > new Coordinate(globalLocation?.Latitude ?? 0, globalLocation?.Longitude ?? 0).DistanceTo(questCoord);
                    var subscriptionDistanceMatches = subscriptionLocation?.DistanceM > 0 && subscriptionLocation?.DistanceM > new Coordinate(subscriptionLocation?.Latitude ?? 0, subscriptionLocation?.Longitude ?? 0).DistanceTo(questCoord);
                    var geofenceMatches = subQuest.Areas.Select(x => x.ToLower()).Contains(geofence.Name.ToLower());

                    // If set distance does not match and no geofences match, then skip Pokemon...
                    if (!globalDistanceMatches && !subscriptionDistanceMatches && !geofenceMatches)
                        continue;

                    var embed = quest.GenerateEmbedMessage(new AlarmMessageSettings
                    {
                        GuildId = user.GuildId,
                        Client = client,
                        Config = _config,
                        Alarm = null,
                        City = geofence.Name,
                    });
                    //var end = DateTime.Now.Subtract(start);
                    //_logger.Debug($"Took {end} to process quest subscription for user {user.UserId}");
                    embed.Embeds.ForEach(x => EnqueueEmbed(new NotificationItem
                    {
                        Subscription = user,
                        Member = member,
                        Embed = x.GenerateDiscordMessage(),
                        Description = questName,
                        City = geofence.Name,
                    }));

                    _statsService.TotalQuestSubscriptionsSent++;
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error: {ex}");
                }
            }

            subscriptions.Clear();
            subscriptions = null;
            user = null;

            await Task.CompletedTask;
        }

        public async Task ProcessInvasionSubscription(PokestopData pokestop)
        {
            // Cache the result per-guild so that geospatial stuff isn't queried for every single subscription below
            var locationCache = new Dictionary<ulong, Geofence>();
            var invasionCoord = new Coordinate(pokestop.Latitude, pokestop.Longitude);
            Geofence GetGeofence(ulong guildId)
            {
                if (!locationCache.TryGetValue(guildId, out var geofence))
                {
                    var geofences = _config.Instance.Servers[guildId].Geofences;
                    geofence = GeofenceService.GetGeofence(geofences, invasionCoord);
                    locationCache.Add(guildId, geofence);
                }
                return geofence;
            }

            var invasion = MasterFile.Instance.GruntTypes.ContainsKey(pokestop.GruntType) ? MasterFile.Instance.GruntTypes[pokestop.GruntType] : null;
            var encounters = invasion?.GetEncounterRewards();
            if (encounters == null)
                return;

            var subscriptions = _subscriptionManager.GetSubscriptionsByInvasion(pokestop?.Name, pokestop?.GruntType ?? InvasionCharacter.CharacterUnset, encounters);
            if (subscriptions == null)
            {
                _logger.LogWarning($"Failed to get subscriptions from database table.");
                return;
            }

            if (!MasterFile.Instance.GruntTypes.ContainsKey(pokestop.GruntType))
            {
                //_logger.Error($"Failed to parse grunt type {pokestop.GruntType}, not in `grunttype.json` list.");
                return;
            }

            Subscription user;
            for (int i = 0; i < subscriptions.Count; i++)
            {
                //var start = DateTime.Now;
                try
                {
                    user = subscriptions[i];

                    if (!_config.Instance.Servers.ContainsKey(user.GuildId))
                        continue;

                    if (!_config.Instance.Servers[user.GuildId].Subscriptions.Enabled)
                        continue;

                    if (!_discordService.DiscordClients.ContainsKey(user.GuildId))
                        continue;

                    var client = _discordService.DiscordClients[user.GuildId];

                    var member = await client.GetMemberById(user.GuildId, user.UserId);
                    if (member == null)
                    {
                        _logger.LogWarning($"Failed to find member with id {user.UserId}.");
                        continue;
                    }

                    if (!member.HasSupporterRole(_config.Instance.Servers[user.GuildId].DonorRoleIds.Keys.ToList()))
                    {
                        _logger.LogInformation($"User {user.UserId} is not a supporter, skipping Team Rocket invasion {pokestop.Name}...");
                        // Automatically disable users subscriptions if not supporter to prevent issues
                        //user.Enabled = false;
                        //user.Save(false);
                        continue;
                    }

                    // Check donor role access for Invasions
                    if (!member.HasRoleAccess(_config.Instance.Servers[user.GuildId].DonorRoleIds, SubscriptionAccessType.Invasions))
                        continue;

                    var subInvasion = user.Invasions.FirstOrDefault(x => x.RewardPokemonId.Intersects(encounters));
                    // Not subscribed to invasion
                    if (subInvasion == null)
                    {
                        //_logger.Debug($"Skipping notification for user {member.DisplayName} ({member.Id}) for raid boss {pokemon.Name}, raid is in city '{loc.Name}'.");
                        continue;
                    }

                    var geofence = GetGeofence(user.GuildId);
                    if (geofence == null)
                    {
                        //_logger.Warn($"Failed to lookup city from coordinates {pkmn.Latitude},{pkmn.Longitude} {db.Pokemon[pkmn.Id].Name} {pkmn.IV}, skipping...");
                        continue;
                    }

                    var globalLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, user.Location, true) == 0);
                    var subscriptionLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, subInvasion.Location, true) == 0);
                    var globalDistanceMatches = globalLocation?.DistanceM > 0 && globalLocation?.DistanceM > new Coordinate(globalLocation?.Latitude ?? 0, globalLocation?.Longitude ?? 0).DistanceTo(invasionCoord);
                    var subscriptionDistanceMatches = subscriptionLocation?.DistanceM > 0 && subscriptionLocation?.DistanceM > new Coordinate(subscriptionLocation?.Latitude ?? 0, subscriptionLocation?.Longitude ?? 0).DistanceTo(invasionCoord);
                    var geofenceMatches = subInvasion.Areas.Select(x => x.ToLower()).Contains(geofence.Name.ToLower());

                    // If set distance does not match and no geofences match, then skip Pokemon...
                    if (!globalDistanceMatches && !subscriptionDistanceMatches && !geofenceMatches)
                        continue;

                    var embed = pokestop.GenerateEmbedMessage(new AlarmMessageSettings
                    {
                        GuildId = user.GuildId,
                        Client = client,
                        Config = _config,
                        Alarm = null,
                        City = geofence?.Name,
                    });
                    //var end = DateTime.Now.Subtract(start);
                    //_logger.LogDebug($"Took {end} to process invasion subscription for user {user.UserId}");
                    embed.Embeds.ForEach(x => EnqueueEmbed(new NotificationItem
                    {
                        Subscription = user,
                        Member = member,
                        Embed = x.GenerateDiscordMessage(),
                        Description = pokestop.Name,
                        City = geofence.Name,
                    }));

                    _statsService.TotalInvasionSubscriptionsSent++;
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error: {ex}");
                }
            }

            subscriptions.Clear();
            subscriptions = null;
            user = null;

            await Task.CompletedTask;
        }

        public async Task ProcessLureSubscription(PokestopData pokestop)
        {
            // Cache the result per-guild so that geospatial stuff isn't queried for every single subscription below
            var locationCache = new Dictionary<ulong, Geofence>();
            var lureCoord = new Coordinate(pokestop.Latitude, pokestop.Longitude);
            Geofence GetGeofence(ulong guildId)
            {
                if (!locationCache.TryGetValue(guildId, out var geofence))
                {
                    var geofences = _config.Instance.Servers[guildId].Geofences;
                    geofence = GeofenceService.GetGeofence(geofences, lureCoord);
                    locationCache.Add(guildId, geofence);
                }
                return geofence;
            }

            var subscriptions = _subscriptionManager.GetSubscriptionsByLure(pokestop.Name, pokestop.LureType);
            if (subscriptions == null)
            {
                _logger.LogWarning($"Failed to get subscriptions from database table.");
                return;
            }

            Subscription user;
            for (int i = 0; i < subscriptions.Count; i++)
            {
                //var start = DateTime.Now;
                try
                {
                    user = subscriptions[i];

                    if (!_config.Instance.Servers.ContainsKey(user.GuildId))
                        continue;

                    if (!_config.Instance.Servers[user.GuildId].Subscriptions.Enabled)
                        continue;

                    if (!_discordService.DiscordClients.ContainsKey(user.GuildId))
                        continue;

                    var client = _discordService.DiscordClients[user.GuildId];

                    var member = await client.GetMemberById(user.GuildId, user.UserId);
                    if (member == null)
                    {
                        _logger.LogWarning($"Failed to find member with id {user.UserId}.");
                        continue;
                    }

                    if (!member.HasSupporterRole(_config.Instance.Servers[user.GuildId].DonorRoleIds.Keys.ToList()))
                    {
                        _logger.LogInformation($"User {user.UserId} is not a supporter, skipping Pokestop lure {pokestop.Name}...");
                        // Automatically disable users subscriptions if not supporter to prevent issues
                        //user.Enabled = false;
                        //user.Save(false);
                        continue;
                    }

                    // Check donor role access for Lures
                    if (!member.HasRoleAccess(_config.Instance.Servers[user.GuildId].DonorRoleIds, SubscriptionAccessType.Lures))
                        continue;

                    var subLure = user.Lures.FirstOrDefault(x => x.LureType == pokestop.LureType);
                    // Not subscribed to lure
                    if (subLure == null)
                    {
                        //_logger.Debug($"Skipping notification for user {member.DisplayName} ({member.Id}) for Pokestop lure {pokemon.Name}, lure is in city '{loc.Name}'.");
                        continue;
                    }

                    var geofence = GetGeofence(user.GuildId);
                    if (geofence == null)
                    {
                        //_logger.Warn($"Failed to lookup city from coordinates {pokestop.Latitude},{pokestop.Longitude} {pokestop.PokestopId} {pokestop.Name}, skipping...");
                        continue;
                    }

                    var globalLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, user.Location, true) == 0);
                    var subscriptionLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, subLure.Location, true) == 0);
                    var globalDistanceMatches = globalLocation?.DistanceM > 0 && globalLocation?.DistanceM > new Coordinate(globalLocation?.Latitude ?? 0, globalLocation?.Longitude ?? 0).DistanceTo(lureCoord);
                    var subscriptionDistanceMatches = subscriptionLocation?.DistanceM > 0 && subscriptionLocation?.DistanceM > new Coordinate(subscriptionLocation?.Latitude ?? 0, subscriptionLocation?.Longitude ?? 0).DistanceTo(lureCoord);
                    var geofenceMatches = subLure.Areas.Select(x => x.ToLower()).Contains(geofence.Name.ToLower());

                    // If set distance does not match and no geofences match, then skip Pokemon...
                    if (!globalDistanceMatches && !subscriptionDistanceMatches && !geofenceMatches)
                        continue;

                    var embed = pokestop.GenerateEmbedMessage(new AlarmMessageSettings
                    {
                        GuildId = user.GuildId,
                        Client = client,
                        Config = _config,
                        Alarm = null,
                        City = geofence.Name,
                    });
                    //var end = DateTime.Now.Subtract(start);
                    //_logger.Debug($"Took {end} to process lure subscription for user {user.UserId}");
                    embed.Embeds.ForEach(x => EnqueueEmbed(new NotificationItem
                    {
                        Subscription = user,
                        Member = member,
                        Embed = x.GenerateDiscordMessage(),
                        Description = pokestop.Name,
                        City = geofence.Name,
                    }));

                    _statsService.TotalLureSubscriptionsSent++;
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error: {ex}");
                }
            }

            subscriptions.Clear();
            subscriptions = null;
            user = null;

            await Task.CompletedTask;
        }

        public async Task ProcessGymSubscription(RaidData raid)
        {
            // Cache the result per-guild so that geospatial stuff isn't queried for every single subscription below
            var locationCache = new Dictionary<ulong, Geofence>();
            var gymCoord = new Coordinate(raid.Latitude, raid.Longitude);
            Geofence GetGeofence(ulong guildId)
            {
                if (!locationCache.TryGetValue(guildId, out var geofence))
                {
                    var geofences = _config.Instance.Servers[guildId].Geofences;
                    geofence = GeofenceService.GetGeofence(geofences, gymCoord);
                    locationCache.Add(guildId, geofence);
                }
                return geofence;
            }

            var subscriptions = _subscriptionManager.GetSubscriptionsByGymName(raid.GymName);
            if (subscriptions == null)
            {
                _logger.LogWarning($"Failed to get subscriptions from database table.");
                return;
            }

            Subscription user;
            var pokemon = MasterFile.GetPokemon(raid.PokemonId, raid.Form);
            for (int i = 0; i < subscriptions.Count; i++)
            {
                //var start = DateTime.Now;
                try
                {
                    user = subscriptions[i];

                    if (!_config.Instance.Servers.ContainsKey(user.GuildId))
                        continue;

                    if (!_config.Instance.Servers[user.GuildId].Subscriptions.Enabled)
                        continue;

                    if (!_discordService.DiscordClients.ContainsKey(user.GuildId))
                        continue;

                    var client = _discordService.DiscordClients[user.GuildId];

                    var member = await client.GetMemberById(user.GuildId, user.UserId);
                    if (member == null)
                    {
                        _logger.LogWarning($"Failed to find member with id {user.UserId}.");
                        continue;
                    }

                    if (!member.HasSupporterRole(_config.Instance.Servers[user.GuildId].DonorRoleIds.Keys.ToList()))
                    {
                        _logger.LogInformation($"User {user.UserId} is not a supporter, skipping raid boss {pokemon.Name} for gym {raid.GymName}...");
                        // Automatically disable users subscriptions if not supporter to prevent issues
                        //user.Enabled = false;
                        //user.Save(false);
                        continue;
                    }

                    // Check donor role access for Gyms
                    if (!member.HasRoleAccess(_config.Instance.Servers[user.GuildId].DonorRoleIds, SubscriptionAccessType.Gyms))
                        continue;

                    var geofence = GetGeofence(user.GuildId);
                    if (geofence == null)
                    {
                        //_logger.Warn($"Failed to lookup city from coordinates {pkmn.Latitude},{pkmn.Longitude} {db.Pokemon[pkmn.Id].Name} {pkmn.IV}, skipping...");
                        continue;
                    }

                    var gymSub = user.Gyms.FirstOrDefault(x => string.Compare(x.Name, raid.GymName, true) == 0);
                    if (gymSub == null)
                        continue;

                    var checkLevel = gymSub.MinimumLevel > 0 && gymSub.MaximumLevel > 0;
                    var containsPokemon = gymSub.PokemonIDs?.Contains(raid.PokemonId) ?? false;
                    if (!checkLevel && !containsPokemon)
                        continue;

                    if (!raid.IsExEligible && gymSub.IsExEligible)
                    {
                        // Skip raids that are not ex eligible when we want ex eligible raids
                        continue;
                    }

                    var globalLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, user.Location, true) == 0);
                    var gymLocation = user.Locations?.FirstOrDefault(x => string.Compare(x.Name, gymSub.Location, true) == 0);
                    var globalDistanceMatches = globalLocation.DistanceM > 0 && globalLocation.DistanceM > new Coordinate(globalLocation.Latitude, globalLocation.Longitude).DistanceTo(gymCoord);
                    var gymDistanceMatches = gymLocation.DistanceM > 0 && gymLocation.DistanceM > new Coordinate(gymLocation.Latitude, gymLocation.Longitude).DistanceTo(gymCoord);
                    //var geofenceMatches = gymSub.Areas.Select(x => x.ToLower()).Contains(geofence.Name.ToLower());
                    // If set distance does not match and no geofences match, then skip Pokemon...
                    if (!globalDistanceMatches && !gymDistanceMatches)
                        continue;

                    var embed = raid.GenerateEmbedMessage(new AlarmMessageSettings
                    {
                        GuildId = user.GuildId,
                        Client = client,
                        Config = _config,
                        Alarm = null,
                        City = geofence.Name,
                    });
                    //var end = DateTime.Now;
                    //_logger.Debug($"Took {end} to process gym raid subscription for user {user.UserId}");
                    embed.Embeds.ForEach(x => EnqueueEmbed(new NotificationItem
                    {
                        Subscription = user,
                        Member = member,
                        Embed = x.GenerateDiscordMessage(),
                        Description = pokemon.Name,
                        City = geofence.Name,
                    }));

                    _statsService.TotalGymSubscriptionsSent++;
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error: {ex}");
                }
            }

            subscriptions.Clear();
            subscriptions = null;
            user = null;

            await Task.CompletedTask;
        }

        private void EnqueueEmbed(NotificationItem embed)
        {
            _queue.Add(embed);
        }
    }
}