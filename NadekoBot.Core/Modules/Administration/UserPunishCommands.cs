using CommandLine;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using NadekoBot.Common.Attributes;
using NadekoBot.Core.Common;
using NadekoBot.Core.Common.TypeReaders.Models;
using NadekoBot.Core.Services.Database.Models;
using NadekoBot.Extensions;
using NadekoBot.Modules.Administration.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using NadekoBot.Modules.Permissions.Services;
using Serilog;

namespace NadekoBot.Modules.Administration
{
    public partial class Administration
    {
        [Group]
        public class UserPunishCommands : NadekoSubmodule<UserPunishService>
        {
            private readonly MuteService _mute;
            private readonly BlacklistService _blacklistService;

            public UserPunishCommands(MuteService mute, BlacklistService blacklistService)
            {
                _mute = mute;
                _blacklistService = blacklistService;
            }

            private async Task<bool> CheckRoleHierarchy(IGuildUser target)
            {
                var curUser = ((SocketGuild) ctx.Guild).CurrentUser;
                var ownerId = Context.Guild.OwnerId;
                var modMaxRole = ((IGuildUser) ctx.User).GetRoles().Max(r => r.Position);
                var targetMaxRole = target.GetRoles().Max(r => r.Position);
                var botMaxRole = curUser.GetRoles().Max(r => r.Position);
                // bot can't punish a user who is higher in the hierarchy. Discord will return 403
                // moderator can be owner, in which case role hierarchy doesn't matter
                // otherwise, moderator has to have a higher role
                if ((botMaxRole <= targetMaxRole || (Context.User.Id != ownerId && targetMaxRole >= modMaxRole)) || target.Id == ownerId)
                {
                    await ReplyErrorLocalizedAsync("hierarchy");
                    return false;
                }
                
                return true;
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            public async Task Warn(IGuildUser user, [Leftover] string reason = null)
            {
                if (!await CheckRoleHierarchy(user))
                    return;

                var dmFailed = false;
                try
                {
                    await (await user.GetOrCreateDMChannelAsync().ConfigureAwait(false)).EmbedAsync(new EmbedBuilder().WithErrorColor()
                                     .WithDescription(GetText("warned_on", ctx.Guild.ToString()))
                                     .AddField(efb => efb.WithName(GetText("moderator")).WithValue(ctx.User.ToString()))
                                     .AddField(efb => efb.WithName(GetText("reason")).WithValue(reason ?? "-")))
                        .ConfigureAwait(false);
                }
                catch
                {
                    dmFailed = true;
                }

                WarningPunishment punishment;
                try
                {
                    punishment = await _service.Warn(ctx.Guild, user.Id, ctx.User, reason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex.Message);
                    var errorEmbed = new EmbedBuilder()
                        .WithErrorColor()
                        .WithDescription(GetText("cant_apply_punishment"));
                    
                    if (dmFailed)
                    {
                        errorEmbed.WithFooter("⚠️ " + GetText("unable_to_dm_user"));
                    }
                    
                    await ctx.Channel.EmbedAsync(errorEmbed);
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithOkColor();
                if (punishment == null)
                {
                    embed.WithDescription(GetText("user_warned",
                        Format.Bold(user.ToString())));
                }
                else
                {
                    embed.WithDescription(GetText("user_warned_and_punished", Format.Bold(user.ToString()),
                        Format.Bold(punishment.Punishment.ToString())));
                }

                if (dmFailed)
                {
                    embed.WithFooter("⚠️ " + GetText("unable_to_dm_user"));
                }

                await ctx.Channel.EmbedAsync(embed);
            }

            public class WarnExpireOptions : INadekoCommandOptions
            {
                [Option('d', "delete", Default = false, HelpText = "Delete warnings instead of clearing them.")]
                public bool Delete { get; set; } = false;
                public void NormalizeOptions()
                {

                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.Administrator)]
            [NadekoOptions(typeof(WarnExpireOptions))]
            [Priority(1)]
            public async Task WarnExpire()
            {
                var expireDays = await _service.GetWarnExpire(ctx.Guild.Id);

                if (expireDays == 0)
                    await ReplyConfirmLocalizedAsync("warns_dont_expire");
                else
                    await ReplyConfirmLocalizedAsync("warns_expire_in", expireDays);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.Administrator)]
            [NadekoOptions(typeof(WarnExpireOptions))]
            [Priority(2)]
            public async Task WarnExpire(int days, params string[] args)
            {
                if (days < 0 || days > 366)
                    return;

                var opts = OptionsParser.ParseFrom<WarnExpireOptions>(args);

                await Context.Channel.TriggerTypingAsync().ConfigureAwait(false);

                await _service.WarnExpireAsync(ctx.Guild.Id, days, opts.Delete).ConfigureAwait(false);
                if(days == 0)
                {
                    await ReplyConfirmLocalizedAsync("warn_expire_reset").ConfigureAwait(false);
                    return;
                }

                if (opts.Delete)
                {
                    await ReplyConfirmLocalizedAsync("warn_expire_set_delete", Format.Bold(days.ToString())).ConfigureAwait(false);
                }
                else
                {
                    await ReplyConfirmLocalizedAsync("warn_expire_set_clear", Format.Bold(days.ToString())).ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [Priority(2)]
            public Task Warnlog(int page, [Leftover] IGuildUser user = null)
            {
                user ??= (IGuildUser) ctx.User;

                return Warnlog(page, user.Id);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [Priority(3)]
            public Task Warnlog(IGuildUser user = null)
            {
                user ??= (IGuildUser) ctx.User;

                return ctx.User.Id == user.Id || ((IGuildUser)ctx.User).GuildPermissions.BanMembers ? Warnlog(user.Id) : Task.CompletedTask;
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [Priority(0)]
            public Task Warnlog(int page, ulong userId)
                => InternalWarnlog(userId, page - 1);

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [Priority(1)]
            public Task Warnlog(ulong userId)
                => InternalWarnlog(userId, 0);

            private async Task InternalWarnlog(ulong userId, int inputPage)
            {
                if (inputPage < 0)
                    return;
                
                var allWarnings = _service.UserWarnings(ctx.Guild.Id, userId);

                await ctx.SendPaginatedConfirmAsync(inputPage, page =>
                {
                    var warnings = allWarnings
                        .Skip(page * 9)
                        .Take(9)
                        .ToArray();

                    var user = (ctx.Guild as SocketGuild)?.GetUser(userId)?.ToString() ?? userId.ToString();
                    var embed = new EmbedBuilder()
                        .WithOkColor()
                        .WithTitle(GetText("warnlog_for", user));

                    if (!warnings.Any())
                    {
                        embed.WithDescription(GetText("warnings_none"));
                    }
                    else
                    {
                        var i = page * 9;
                        foreach (var w in warnings)
                        {
                            i++;
                            var name = GetText("warned_on_by",
                                w.DateAdded.Value.ToString("dd.MM.yyy"),
                                w.DateAdded.Value.ToString("HH:mm"),
                                w.Moderator);
                            
                            if (w.Forgiven)
                                name = $"{Format.Strikethrough(name)} {GetText("warn_cleared_by", w.ForgivenBy)}";

                            embed.AddField($"#`{i}` " + name, w.Reason.TrimTo(1020));
                        }
                    }

                    return embed;
                }, allWarnings.Length, 9);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            public async Task WarnlogAll(int page = 1)
            {
                if (--page < 0)
                    return;
                var warnings = _service.WarnlogAll(ctx.Guild.Id);

                await ctx.SendPaginatedConfirmAsync(page, (curPage) =>
                {
                    var ws = warnings.Skip(curPage * 15)
                        .Take(15)
                        .ToArray()
                        .Select(x =>
                        {
                            var all = x.Count();
                            var forgiven = x.Count(y => y.Forgiven);
                            var total = all - forgiven;
                            var usr = ((SocketGuild)ctx.Guild).GetUser(x.Key);
                            return (usr?.ToString() ?? x.Key.ToString()) + $" | {total} ({all} - {forgiven})";
                        });

                    return new EmbedBuilder().WithOkColor()
                        .WithTitle(GetText("warnings_list"))
                        .WithDescription(string.Join("\n", ws));
                }, warnings.Length, 15).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            public Task Warnclear(IGuildUser user, int index = 0)
                => Warnclear(user.Id, index);

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            public async Task Warnclear(ulong userId, int index = 0)
            {
                if (index < 0)
                    return;
                var success = await _service.WarnClearAsync(ctx.Guild.Id, userId, index, ctx.User.ToString());
                var userStr = Format.Bold((ctx.Guild as SocketGuild)?.GetUser(userId)?.ToString() ?? userId.ToString());
                if (index == 0)
                {
                    await ReplyConfirmLocalizedAsync("warnings_cleared", userStr).ConfigureAwait(false);
                }
                else
                {
                    if (success)
                    {
                        await ReplyConfirmLocalizedAsync("warning_cleared", Format.Bold(index.ToString()), userStr)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await ReplyErrorLocalizedAsync("warning_clear_fail").ConfigureAwait(false);
                    }
                }
            }

            public enum AddRole
            {
                AddRole
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [Priority(1)]
            public async Task WarnPunish(int number, AddRole _, IRole role, StoopidTime time = null)
            {
                var punish = PunishmentAction.AddRole;
                var success = _service.WarnPunish(ctx.Guild.Id, number, punish, time, role);

                if (!success)
                    return;

                if (time is null)
                {
                    await ReplyConfirmLocalizedAsync("warn_punish_set",
                        Format.Bold(punish.ToString()),
                        Format.Bold(number.ToString())).ConfigureAwait(false);
                }
                else
                {
                    await ReplyConfirmLocalizedAsync("warn_punish_set_timed",
                        Format.Bold(punish.ToString()),
                        Format.Bold(number.ToString()),
                        Format.Bold(time.Input)).ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            public async Task WarnPunish(int number, PunishmentAction punish, StoopidTime time = null)
            {
                // this should never happen. Addrole has its own method with higher priority
                if (punish == PunishmentAction.AddRole)
                    return;

                var success = _service.WarnPunish(ctx.Guild.Id, number, punish, time);

                if (!success)
                    return;

                if (time is null)
                {
                    await ReplyConfirmLocalizedAsync("warn_punish_set",
                        Format.Bold(punish.ToString()),
                        Format.Bold(number.ToString())).ConfigureAwait(false);
                }
                else
                {
                    await ReplyConfirmLocalizedAsync("warn_punish_set_timed",
                        Format.Bold(punish.ToString()),
                        Format.Bold(number.ToString()),
                        Format.Bold(time.Input)).ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            public async Task WarnPunish(int number)
            {
                if (!_service.WarnPunishRemove(ctx.Guild.Id, number))
                {
                    return;
                }

                await ReplyConfirmLocalizedAsync("warn_punish_rem",
                    Format.Bold(number.ToString())).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task WarnPunishList()
            {
                var ps = _service.WarnPunishList(ctx.Guild.Id);

                string list;
                if (ps.Any())
                {

                    list = string.Join("\n", ps.Select(x => $"{x.Count} -> {x.Punishment} {(x.Punishment == PunishmentAction.AddRole ? $"<@&{x.RoleId}>" : "")} {(x.Time <= 0 ? "" : x.Time.ToString() + "m")} "));
                }
                else
                {
                    list = GetText("warnpl_none");
                }
                await ctx.Channel.SendConfirmAsync(
                    GetText("warn_punish_list"),
                    list).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [BotPerm(GuildPerm.BanMembers)]
            [Priority(1)]
            public async Task Ban(StoopidTime time, IUser user, [Leftover] string msg = null)
            {
                if (time.Time > TimeSpan.FromDays(49))
                    return;

                var guildUser = await ((DiscordSocketClient)Context.Client).Rest.GetGuildUserAsync(Context.Guild.Id, user.Id);

                if (guildUser != null && !await CheckRoleHierarchy(guildUser))
                    return;

                var dmFailed = false;

                if (guildUser != null)
                {
                    try
                    {
                        var defaultMessage = GetText("bandm", Format.Bold(ctx.Guild.Name), msg);
                        var embed = _service.GetBanUserDmEmbed(Context, guildUser, defaultMessage, msg, time.Time);
                        if (!(embed is null))
                        {
                            var userChannel = await guildUser.GetOrCreateDMChannelAsync();
                            await userChannel.EmbedAsync(embed);
                        }
                    }
                    catch
                    {
                        dmFailed = true;
                    }
                }

                await _mute.TimedBan(Context.Guild, user, time.Time, ctx.User.ToString() + " | " + msg).ConfigureAwait(false);
                var toSend = new EmbedBuilder().WithOkColor()
                    .WithTitle("⛔️ " + GetText("banned_user"))
                    .AddField(efb => efb.WithName(GetText("username")).WithValue(user.ToString()).WithIsInline(true))
                    .AddField(efb => efb.WithName("ID").WithValue(user.Id.ToString()).WithIsInline(true))
                    .AddField(efb => efb.WithName(GetText("duration")).WithValue($"{time.Time.Days}d {time.Time.Hours}h {time.Time.Minutes}m").WithIsInline(true));

                if (dmFailed)
                {
                    toSend.WithFooter("⚠️ " + GetText("unable_to_dm_user"));
                }

                await ctx.Channel.EmbedAsync(toSend)
                    .ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [BotPerm(GuildPerm.BanMembers)]
            [Priority(0)]
            public async Task Ban(ulong userId, [Leftover] string msg = null)
            {
                var user = await ((DiscordSocketClient)Context.Client).Rest.GetGuildUserAsync(Context.Guild.Id, userId);
                if (user is null)
                {
                    await ctx.Guild.AddBanAsync(userId, 7, ctx.User.ToString() + " | " + msg);
                    
                    await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                            .WithTitle("⛔️ " + GetText("banned_user"))
                            .AddField(efb => efb.WithName("ID").WithValue(userId.ToString()).WithIsInline(true)))
                        .ConfigureAwait(false);
                }
                else
                {
                    await Ban(user, msg);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [BotPerm(GuildPerm.BanMembers)]
            [Priority(2)]
            public async Task Ban(IGuildUser user, [Leftover] string msg = null)
            {
                if (!await CheckRoleHierarchy(user))
                    return;

                var dmFailed = false;

                try
                {
                    var defaultMessage = GetText("bandm", Format.Bold(ctx.Guild.Name), msg);
                    var embed = _service.GetBanUserDmEmbed(Context, user, defaultMessage, msg, null);
                    if (!(embed is null))
                    {
                        var userChannel = await user.GetOrCreateDMChannelAsync();
                        await userChannel.EmbedAsync(embed);
                    }
                }
                catch
                {
                    dmFailed = true;
                }

                await ctx.Guild.AddBanAsync(user, 7, ctx.User.ToString() + " | " + msg).ConfigureAwait(false);

                var toSend = new EmbedBuilder().WithOkColor()
                    .WithTitle("⛔️ " + GetText("banned_user"))
                    .AddField(efb => efb.WithName(GetText("username")).WithValue(user.ToString()).WithIsInline(true))
                    .AddField(efb => efb.WithName("ID").WithValue(user.Id.ToString()).WithIsInline(true));

                if (dmFailed)
                {
                    toSend.WithFooter("⚠️ " + GetText("unable_to_dm_user"));
                }
                
                await ctx.Channel.EmbedAsync(toSend)
                    .ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [BotPerm(GuildPerm.BanMembers)]
            public async Task BanMessage([Leftover] string message = null)
            {
                if (message is null)
                {
                    var template = _service.GetBanTemplate(Context.Guild.Id);
                    if (template is null)
                    {
                        await ReplyConfirmLocalizedAsync("banmsg_default");
                        return;
                    }

                    await Context.Channel.SendConfirmAsync(template);
                    return;
                }
                
                _service.SetBanTemplate(Context.Guild.Id, message);
                await ctx.OkAsync();
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [BotPerm(GuildPerm.BanMembers)]
            public async Task BanMsgReset()
            {
                _service.SetBanTemplate(Context.Guild.Id, null);
                await ctx.OkAsync();
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [BotPerm(GuildPerm.BanMembers)]
            [Priority(0)]
            public Task BanMessageTest([Leftover] string reason = null)
                => InternalBanMessageTest(reason, null);
            
            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [BotPerm(GuildPerm.BanMembers)]
            [Priority(1)]
            public Task BanMessageTest(StoopidTime duration, [Leftover] string reason = null)
                => InternalBanMessageTest(reason, duration.Time);
            
            private async Task InternalBanMessageTest(string reason, TimeSpan? duration)
            {
                var dmChannel = await ctx.User.GetOrCreateDMChannelAsync();
                var defaultMessage = GetText("bandm", Format.Bold(ctx.Guild.Name), reason);
                var crEmbed = _service.GetBanUserDmEmbed(Context,
                    (IGuildUser)Context.User,
                    defaultMessage,
                    reason,
                    duration);

                if (crEmbed is null)
                {
                    await ConfirmLocalizedAsync("bandm_disabled");
                }
                else
                {
                    try
                    {
                        await dmChannel.EmbedAsync(crEmbed);
                    }
                    catch (Exception)
                    {
                        await ReplyErrorLocalizedAsync("unable_to_dm_user");
                        return;
                    }

                    await Context.OkAsync();
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [BotPerm(GuildPerm.BanMembers)]
            public async Task Unban([Leftover] string user)
            {
                var bans = await ctx.Guild.GetBansAsync().ConfigureAwait(false);

                var bun = bans.FirstOrDefault(x => x.User.ToString().ToLowerInvariant() == user.ToLowerInvariant());

                if (bun == null)
                {
                    await ReplyErrorLocalizedAsync("user_not_found").ConfigureAwait(false);
                    return;
                }

                await UnbanInternal(bun.User).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [BotPerm(GuildPerm.BanMembers)]
            public async Task Unban(ulong userId)
            {
                var bans = await ctx.Guild.GetBansAsync().ConfigureAwait(false);

                var bun = bans.FirstOrDefault(x => x.User.Id == userId);

                if (bun == null)
                {
                    await ReplyErrorLocalizedAsync("user_not_found").ConfigureAwait(false);
                    return;
                }

                await UnbanInternal(bun.User).ConfigureAwait(false);
            }

            private async Task UnbanInternal(IUser user)
            {
                await ctx.Guild.RemoveBanAsync(user).ConfigureAwait(false);

                await ReplyConfirmLocalizedAsync("unbanned_user", Format.Bold(user.ToString())).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.KickMembers | GuildPerm.ManageMessages)]
            [BotPerm(GuildPerm.BanMembers)]
            public Task Softban(IGuildUser user, [Leftover] string msg = null)
                => SoftbanInternal(user, msg);

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.KickMembers | GuildPerm.ManageMessages)]
            [BotPerm(GuildPerm.BanMembers)]
            public async Task Softban(ulong userId, [Leftover] string msg = null)
            {
                var user = await ((DiscordSocketClient)Context.Client).Rest.GetGuildUserAsync(Context.Guild.Id, userId);
                if (user is null)
                    return;

                await SoftbanInternal(user);
            }
            
            private async Task SoftbanInternal(IGuildUser user, [Leftover] string msg = null)
            {
                if (!await CheckRoleHierarchy(user))
                    return;

                var dmFailed = false;

                try
                {
                    await user.SendErrorAsync(GetText("sbdm", Format.Bold(ctx.Guild.Name), msg)).ConfigureAwait(false);
                }
                    catch
                {
                    dmFailed = true;
                }

                await ctx.Guild.AddBanAsync(user, 7, "Softban | " + ctx.User.ToString() + " | " + msg).ConfigureAwait(false);
                try { await ctx.Guild.RemoveBanAsync(user).ConfigureAwait(false); }
                catch { await ctx.Guild.RemoveBanAsync(user).ConfigureAwait(false); }

                var toSend = new EmbedBuilder().WithOkColor()
                    .WithTitle("☣ " + GetText("sb_user"))
                    .AddField(efb => efb.WithName(GetText("username")).WithValue(user.ToString()).WithIsInline(true))
                    .AddField(efb => efb.WithName("ID").WithValue(user.Id.ToString()).WithIsInline(true));
                
                if (dmFailed)
                {
                    toSend.WithFooter("⚠️ " + GetText("unable_to_dm_user"));
                }
                
                await ctx.Channel.EmbedAsync(toSend)
                    .ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.KickMembers)]
            [BotPerm(GuildPerm.KickMembers)]
            [Priority(1)]
            public Task Kick(IGuildUser user, [Leftover] string msg = null)
                => KickInternal(user, msg);

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.KickMembers)]
            [BotPerm(GuildPerm.KickMembers)]
            [Priority(0)]
            public async Task Kick(ulong userId, [Leftover] string msg = null)
            {
                var user = await ((DiscordSocketClient)Context.Client).Rest.GetGuildUserAsync(Context.Guild.Id, userId);
                if (user is null)
                    return;
                
                await KickInternal(user, msg);
            }

            public async Task KickInternal(IGuildUser user, string msg = null)
            {
                if (!await CheckRoleHierarchy(user))
                    return;

                var dmFailed = false;

                try
                {
                    await user.SendErrorAsync(GetText("kickdm", Format.Bold(ctx.Guild.Name), msg))
                        .ConfigureAwait(false);
                }
                catch
                {                        
                    dmFailed = true;
                }
            
                await user.KickAsync(ctx.User.ToString() + " | " + msg).ConfigureAwait(false);
                
                var toSend = new EmbedBuilder().WithOkColor()
                    .WithTitle(GetText("kicked_user"))
                    .AddField(efb => efb.WithName(GetText("username")).WithValue(user.ToString()).WithIsInline(true))
                    .AddField(efb => efb.WithName("ID").WithValue(user.Id.ToString()).WithIsInline(true));

                if (dmFailed)
                {
                    toSend.WithFooter("⚠️ " + GetText("unable_to_dm_user"));
                }
                
                await ctx.Channel.EmbedAsync(toSend)
                    .ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.BanMembers)]
            [BotPerm(GuildPerm.BanMembers)]
            [OwnerOnly]
            public async Task MassKill([Leftover] string people)
            {
                if (string.IsNullOrWhiteSpace(people))
                    return;

                var (bans, missing) = _service.MassKill((SocketGuild)ctx.Guild, people);

                var missStr = string.Join("\n", missing);
                if (string.IsNullOrWhiteSpace(missStr))
                    missStr = "-";

                //send a message but don't wait for it
                var banningMessageTask = ctx.Channel.EmbedAsync(new EmbedBuilder()
                    .WithDescription(GetText("mass_kill_in_progress", bans.Count()))
                    .AddField(GetText("invalid", missing), missStr)
                    .WithOkColor());

                //do the banning
                await Task.WhenAll(bans
                    .Where(x => x.Id.HasValue)
                    .Select(x => ctx.Guild.AddBanAsync(x.Id.Value, 7, x.Reason, new RequestOptions()
                    {
                        RetryMode = RetryMode.AlwaysRetry,
                    })))
                    .ConfigureAwait(false);

                //wait for the message and edit it
                var banningMessage = await banningMessageTask.ConfigureAwait(false);

                await banningMessage.ModifyAsync(x => x.Embed = new EmbedBuilder()
                    .WithDescription(GetText("mass_kill_completed", bans.Count()))
                    .AddField(GetText("invalid", missing), missStr)
                    .WithOkColor()
                    .Build()).ConfigureAwait(false);
            }
        }
    }
}
