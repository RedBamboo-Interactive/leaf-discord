using System.Reflection;
using Discord;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordChannelPermissionModelTests
{
    [Fact]
    public void A_channel_type_DiscordNet_does_not_model_is_left_out_of_the_map()
    {
        // A bare IChannel is what Discord.Net hands back for a type it does not know, and its
        // ChannelPermissions.All throws "Unknown channel type" for it.
        Assert.False(DiscordBotRuntime.HasModeledPermissions(Proxy<IChannel>()));
        Assert.Throws<ArgumentException>(() => ChannelPermissions.All(Proxy<IChannel>()));
    }

    [Fact]
    public void A_thread_whose_parent_is_not_cached_is_left_out_of_the_map()
        => Assert.False(DiscordBotRuntime.HasModeledPermissions(null));

    [Fact]
    public void Text_forum_voice_stage_media_and_category_channels_stay_in_the_map()
    {
        Assert.True(DiscordBotRuntime.HasModeledPermissions(Proxy<ITextChannel>()));
        Assert.True(DiscordBotRuntime.HasModeledPermissions(Proxy<IForumChannel>()));
        Assert.True(DiscordBotRuntime.HasModeledPermissions(Proxy<IVoiceChannel>()));
        Assert.True(DiscordBotRuntime.HasModeledPermissions(Proxy<IStageChannel>()));
        Assert.True(DiscordBotRuntime.HasModeledPermissions(Proxy<IMediaChannel>()));
        Assert.True(DiscordBotRuntime.HasModeledPermissions(Proxy<ICategoryChannel>()));
    }

    private static T Proxy<T>() where T : class => DispatchProxy.Create<T, InertProxy>();

    public class InertProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
