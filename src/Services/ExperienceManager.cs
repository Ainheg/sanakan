﻿#pragma warning disable 1591

using Discord.WebSocket;
using Sanakan.Config;
using Sanakan.Extensions;
using Sanakan.Services.Executor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Sanakan.Services
{
    public class ExperienceManager
    {
        private const double SAVE_AT = 3;
        private const double LM = 0.35;

        private Dictionary<ulong, double> _exp;
        private Dictionary<ulong, ulong> _messages;
        private Dictionary<ulong, ulong> _commands;
        private Dictionary<ulong, ulong> _characters;

        private DiscordSocketClient _client;
        private IExecutor _executor;
        private IConfig _config;

        public ExperienceManager(DiscordSocketClient client, IExecutor executor, IConfig config)
        {
            _executor = executor;
            _client = client;
            _config = config;

            _exp = new Dictionary<ulong, double>();
            _messages = new Dictionary<ulong, ulong>();
            _commands = new Dictionary<ulong, ulong>();
            _characters = new Dictionary<ulong, ulong>();
#if !DEBUG
            _client.MessageReceived += HandleMessageAsync;
#endif
        }

        public long CalculateExpForLevel(long level) => (level <= 0) ? 0 : Convert.ToInt64(Math.Floor(Math.Pow(level / LM, 2)) + 1);
        
        public long CalculateLevel(long exp) => Convert.ToInt64(Math.Floor(LM * Math.Sqrt(exp)));

        public async Task NotifyAboutLevelAsync(SocketGuildUser user, ISocketMessageChannel channel, long level)
        {
            await channel.SendMessageAsync("", embed: $"{user.Nickname ?? user.Username} awansował na {level} poziom!".ToEmbedMessage(EMType.Bot).Build());
        }

        private async Task HandleMessageAsync(SocketMessage message)
        {
            if (message.Author.IsBot || message.Author.IsWebhook) return;

            var user = message.Author as SocketGuildUser;
            if (user == null) return;

            using (var db = new Database.GuildConfigContext(_config))
            {
                var config = await db.GetCachedGuildFullConfigAsync(user.Guild.Id);
                if (config != null)
                {
                    if (config.ChannelsWithoutExp != null)
                        if (config.ChannelsWithoutExp.Any(x => x.Channel == message.Channel.Id))
                            return;

                    var role = user.Guild.GetRole(config.UserRole);
                    if (role != null)
                        if (!user.Roles.Contains(role))
                            return;
                }
            }

            CalculateExpAndCreateTask(user, message);
        }

        private void CountMessage(ulong userId, bool isCommand)
        {
            if (!_messages.Any(x => x.Key == userId))
                _messages.Add(userId, 1);
            else 
                _messages[userId]++;

            if (!_commands.Any(x => x.Key == userId))
                _commands.Add(userId, isCommand ? 1u : 0u);
            else 
                if (isCommand)
                    _commands[userId]++;
        }

        private void CountCharacters(ulong userId, ulong characters)
        {
            if (!_characters.Any(x => x.Key == userId))
                _characters.Add(userId, characters);
            else 
                _characters[userId] += characters;
        }

        private void CalculateExpAndCreateTask(SocketGuildUser user, SocketMessage message)
        {
            CountMessage(user.Id, message.Content.IsCommand(_config.Get().Prefix));
            var exp = GetPointsFromMsg(message);

            if (!_exp.Any(x => x.Key == user.Id))
            {
                _exp.Add(message.Author.Id, exp);
                return;
            }

            _exp[user.Id] += exp;

            var saved = _exp[user.Id];
            if (saved < SAVE_AT) return;

            var fullP = (long)Math.Floor(saved);
            _exp[message.Author.Id] -= fullP;

            var task = CreateTask(user, message.Channel, fullP, _messages[user.Id], _commands[user.Id], _characters[user.Id]);
            _characters[user.Id] = 0;
            _messages[user.Id] = 0;
            _commands[user.Id] = 0;

            _executor.TryAdd(new Executable(task), TimeSpan.FromSeconds(1));
        }

        private double GetPointsFromMsg(SocketMessage message)
        {
            int emoteChars = message.Tags.CountEmotesTextLenght();
            int linkChars = message.Content.CountLinkTextLength();
            int nonWhiteSpaceChars = message.Content.Count(c => c != ' ');
            double charsThatMatters = nonWhiteSpaceChars - linkChars - emoteChars;

            CountCharacters(message.Author.Id, (ulong)charsThatMatters);
            return GetExpPointBasedOnCharCount(charsThatMatters);
        }

        private double GetExpPointBasedOnCharCount(double charCount)
        {
            var tmpCnf = _config.Get();
            double cpp = tmpCnf.Exp.CharPerPoint;
            double min = tmpCnf.Exp.MinPerMessage;
            double max = tmpCnf.Exp.MaxPerMessage;

            double experience = charCount / cpp;
            if (experience < min) return min;
            if (experience > max) return max;
            return experience;
        }

        private Task<bool> CreateTask(SocketGuildUser user, ISocketMessageChannel channel, long exp, ulong messages, ulong commands, ulong characters)
        {
            return new Task<bool>(() =>
            {
                using (var db = new Database.UserContext(_config))
                {
                    var usr = db.Users.FirstOrDefault(x => x.Id == user.Id);
                    if (usr == null) return false;

                    if ((DateTime.Now - usr.MeasureDate.AddMonths(1)).TotalSeconds > 1)
                    {
                        usr.MeasureDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                        usr.MessagesCntAtDate = usr.MessagesCnt;
                        usr.CharacterCntFromDate = characters;
                    }
                    else 
                        usr.CharacterCntFromDate += characters;

                    usr.ExpCnt += exp;
                    usr.MessagesCnt += messages;
                    usr.CommandsCnt += commands;

                    var newLevel = CalculateLevel(usr.ExpCnt);
                    if (newLevel != usr.Level)
                    {
                        usr.Level = newLevel;
                        _ = Task.Run(async () => { await NotifyAboutLevelAsync(user, channel, newLevel); });
                    }

                    db.SaveChanges();
                }

                return true;
            });
        }
    }
}
