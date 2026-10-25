using System.Net;
using PrimePC.Gaming;

namespace PrimePC.Tests;

public class GamingTests
{
    /// <summary>Quadros a cada <paramref name="frameMs"/> ms, com uma travada de <paramref name="stallMs"/> a cada N quadros.</summary>
    private static double[] Frames(int count, double frameMs, int stallEvery = 0, double stallMs = 0, double start = 0)
    {
        var t = new double[count];
        double now = start;
        for (int i = 0; i < count; i++)
        {
            t[i] = now;
            now += frameMs + (stallEvery > 0 && (i + 1) % stallEvery == 0 ? stallMs : 0);
        }

        return t;
    }

    [Fact]
    public void SteadyFrameRateGivesFpsAndFrameTime()
    {
        var stats = FrameTracker.Compute(1234, PresentSource.Dxgi, Frames(600, 1000.0 / 144));
        Assert.NotNull(stats);
        Assert.InRange(stats.Fps, 143.5, 144.5);
        Assert.InRange(stats.FrameTimeMs, 6.9, 7.0);
        Assert.InRange(stats.Low1Fps!.Value, 143.5, 144.5);
        Assert.Equal(120, stats.RecentFrameTimes.Length);
    }

    [Fact]
    public void StallsLowerTheOnePercentLowButBarelyTheAverage()
    {
        // 100 FPS com uma travada de 40 ms a cada 100 quadros (como a janela de teste usada no PC).
        var stats = FrameTracker.Compute(1, PresentSource.Dxgi, Frames(1000, 10, stallEvery: 100, stallMs: 40))!;
        Assert.InRange(stats.Fps, 90, 101);
        Assert.InRange(stats.Low1Fps!.Value, 19, 23); // quase só travadas de 50 ms no 1% mais lento → ~20 FPS
        Assert.Contains(stats.RecentFrameTimes, ft => ft is > 49 and < 51);
    }

    [Fact]
    public void FewFramesHaveNoOnePercentLowAndOneFrameHasNoStats()
    {
        var few = FrameTracker.Compute(1, PresentSource.Dxgi, Frames(10, 16.7))!;
        Assert.InRange(few.Fps, 59, 61);
        Assert.Null(few.Low1Fps);
        Assert.Null(FrameTracker.Compute(1, PresentSource.Dxgi, new[] { 5.0 }));

        // Abaixo de 1 FPS: usa o intervalo entre os dois últimos quadros.
        var slow = FrameTracker.Compute(1, PresentSource.Dxgi, new[] { 0.0, 2000.0 })!;
        Assert.InRange(slow.Fps, 0.49, 0.51);
    }

    [Fact]
    public void TrackerPrefersDirectXAndTheBusiestSwapChain()
    {
        var tracker = new FrameTracker();
        // O jogo apresenta a 120 FPS na cadeia principal e a 10 FPS numa secundária; o kernel vê tudo junto.
        foreach (var t in Frames(240, 1000.0 / 120))
        {
            tracker.OnPresent(500, PresentSource.Dxgi, 0xAAAA, t);
            tracker.OnPresent(500, PresentSource.Kernel, 0, t);
        }

        foreach (var t in Frames(20, 100))
            tracker.OnPresent(500, PresentSource.Dxgi, 0xBBBB, t);

        var stats = tracker.GetStats(500, nowMs: 2000)!;
        Assert.Equal(PresentSource.Dxgi, stats.Source);
        Assert.InRange(stats.Fps, 119, 121);
        Assert.True(tracker.IsPresenting(500, 2000));
        Assert.False(tracker.IsPresenting(999, 2000));

        // Vulkan/OpenGL sem DXGI: só o kernel.
        foreach (var t in Frames(120, 1000.0 / 60))
            tracker.OnPresent(600, PresentSource.Kernel, 0, t);
        Assert.Equal(PresentSource.Kernel, tracker.GetStats(600, 2000)!.Source);
    }

    [Fact]
    public void FramesVeryCloseTogetherAreAllCounted()
    {
        // Conferido no PC: a aplicação de teste entrega centenas de quadros reais a menos de 0,2 ms um do outro
        // (quando "recupera o atraso"); todos têm de contar, senão o FPS fica abaixo do real.
        var tracker = new FrameTracker();
        tracker.OnPresent(10, PresentSource.Dxgi, 1, 100);
        tracker.OnPresent(10, PresentSource.Dxgi, 1, 100.07);
        tracker.OnPresent(10, PresentSource.Dxgi, 1, 110);
        var stats = tracker.GetStats(10, 110)!;
        Assert.Equal(2, stats.RecentFrameTimes.Length);
        Assert.InRange(stats.Fps, 199, 201);
    }

    [Fact]
    public void ProcessThatStoppedPresentingIsNoLongerActive()
    {
        var tracker = new FrameTracker();
        foreach (var t in Frames(100, 10))
            tracker.OnPresent(700, PresentSource.Dxgi, 1, t);
        Assert.NotNull(tracker.GetStats(700, 1000));
        Assert.Null(tracker.GetStats(700, 1000 + FrameTracker.StaleAfterMs + 100));
        tracker.Prune(20_000);
        Assert.Null(tracker.GetStats(700, 1000));
        tracker.OnPresent(4, PresentSource.Dxgi, 1, 0); // processo do sistema: ignorado
        Assert.False(tracker.IsPresenting(4, 0, minFrames: 1));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("185.50.104.10", true)]
    [InlineData("192.168.0.10", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("239.255.255.250", false)]
    [InlineData("100.64.1.1", false)]
    [InlineData("169.254.3.4", false)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::1", false)]
    public void OnlyInternetAddressesCountAsGameServers(string address, bool expected) =>
        Assert.Equal(expected, NetworkTracker.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void WindowsNetworkEventAddressesAreDecoded()
    {
        // Valores reais vistos no PC: 16777343 = 127.0.0.1 e -83886097 = 239.255.255.250 (SSDP).
        Assert.Equal("127.0.0.1", NetworkTracker.FromInt32(16777343).ToString());
        Assert.Equal("239.255.255.250", NetworkTracker.FromInt32(-83886097).ToString());
    }

    [Fact]
    public void GameServerIsTheBusiestUdpDestination()
    {
        var net = new NetworkTracker();
        var server = IPAddress.Parse("185.50.104.10");
        var web = IPAddress.Parse("104.16.1.1");
        for (int i = 0; i < 60; i++)
            net.OnSend(900, server, udp: true, i * 16);
        for (int i = 0; i < 200; i++)
            net.OnSend(900, web, udp: false, i * 5); // mais pacotes, mas TCP (ex.: loja/chat do jogo)
        net.OnSend(900, IPAddress.Parse("192.168.0.1"), udp: true, 100);
        net.OnSend(901, IPAddress.Parse("8.8.8.8"), udp: true, 100); // outro processo

        Assert.Equal(server, net.TopEndpoint(900, nowMs: 1000));
        Assert.Null(net.TopEndpoint(900, nowMs: 60_000)); // sem tráfego recente
        Assert.Equal(IPAddress.Parse("8.8.8.8"), net.TopEndpoint(901, 1000));

        net.OnSend(902, IPAddress.Parse("::ffff:185.50.104.11"), udp: true, 0);
        Assert.Equal(IPAddress.Parse("185.50.104.11"), net.TopEndpoint(902, 10));
    }
}
