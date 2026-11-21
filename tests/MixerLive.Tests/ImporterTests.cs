using MixerLive.Core.Audio;
using MixerLive.Core.Import;
using MixerLive.Core.Model;
using Xunit;

namespace MixerLive.Tests;

public class ImporterTests
{
    // Trimmed copy of a real VoicemeeterBanana_LastSettings.xml, including the duplicated
    // <VoiceMeeterParameters> opening tag that Voicemeeter writes (not well-formed XML).
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <VBAudioVoicemeeterSettings>
        <VoiceMeeterDeviceConfiguration>
        	<OptionDev mme='1024' wdm='512' ks='512' asio='0' srasio='0' msA1='0.00' msA2='0.00' msA3='0.00' />
        	<InputDev index='1' type='4' name="Mic/Inst/Line In 1/2 (Studio 24c)" />
        	<InputDev index='2' name="-" />
        	<InputDev index='3' type='1' name="Headset Microphone (CORSAIR HS8" />
        	<OutputDev index='1' type='4' name="Speakers (FiiO KA15(2.0))" />
        	<OutputDev index='2' name="-" pOut1='0' pOut2='0' />
        	<OutputDev index='3' type='1' name="R&amp;D Monitor" />
        </VoiceMeeterDeviceConfiguration>
        <VoiceMeeterParameters>
        <VoiceMeeterParameters>
        	<LabelStrip1>MIC</LabelStrip1>
        	<LabelStrip2>NOT IN USE</LabelStrip2>
        	<LabelStrip3></LabelStrip3>
        	<LabelVirtualStrip1>SYSTEM</LabelVirtualStrip1>
        	<LabelVirtualStrip2>NOT IN USE</LabelVirtualStrip2>
        	<VMOptions MonitorOnSEL='0' SliderRelative='0' />
        	<K7BUSRoute busa='1' busa2='1' busa3='1' busb='1' busb2='1' dblevel='0.00' />
        	<Strip index='1' mute='0' vaio='0' solo='0' mono='1' muc='0' prg_c='0' prg_g='0' busa='0' busa2='1' busa3='0' busb='0' busb2='0' karaoke='0' dblevel='-3.50' dblimit='12.00' />
        	<StripComp index='1' gainin='0.00' attack='10.0' release='60.0' knee='0.50' comprate='2.500' threshold='-40.00' automakeup='1' gainout='0.00' />
        	<StripGate index='1' thresin='-35.00' damping='-50.00' bpsidechain='50' attack='20.0' hold='200.0' release='1250.0' />
        	<Strip index='1' paneltype='1' Panel3Dx='0.000' Panel3Dy='0.000' ColorPanelx='0.000' ColorPanely='0.000' ModPanelx='0.000' ModPanely='0.000' audibility_c='0.00' audibility_g='0.00' />
        	<Strip index='2' mute='1' vaio='0' solo='0' mono='0' muc='0' prg_c='0' prg_g='0' busa='0' busa2='0' busa3='0' busb='0' busb2='0' karaoke='0' dblevel='-60.00' dblimit='12.00' />
        	<StripComp index='2' gainin='0.00' attack='10.0' release='60.0' knee='0.50' comprate='1.000' threshold='-40.00' automakeup='1' gainout='0.00' />
        	<StripGate index='2' thresin='-50.00' damping='-50.00' bpsidechain='50' attack='20.0' hold='200.0' release='1250.0' />
        	<Strip index='4' mute='0' vaio='0' solo='0' mono='0' muc='0' prg_c='0' prg_g='0' busa='1' busa2='1' busa3='0' busb='1' busb2='0' karaoke='0' dblevel='0.00' dblimit='12.00' />
        	<Strip index='4' EQGain1='3.00' EQGain2='-1.50' EQGain3='0.00' Panel3Dx='0.000' Panel3Dy='0.000' />
        	<Strip index='5' mute='1' vaio='0' solo='0' mono='0' muc='0' prg_c='0' prg_g='0' busa='0' busa2='0' busa3='0' busb='0' busb2='0' karaoke='0' dblevel='-60.00' dblimit='12.00' />
        	<Bus index='1' mute='0' vaio='0' mono='0' cross='0' BusMode='0' EQon='1' SEL='0' monitor='1' dblevel='-6.00' />
        	<Bus index='2' mute='1' vaio='0' mono='1' cross='0' BusMode='0' EQon='0' SEL='0' monitor='0' dblevel='0.00' />
        	<LabelBus1>PHONES</LabelBus1>
        	<LabelVirtualBus1>STREAM</LabelVirtualBus1>
        <VoiceMeeterBUSEQ>
        	<Bus index='1' channel='1' cell='1' EQon='1' EQtype='5' dblevel='2.50' freq='80.00' Q='0.70' />
        	<Bus index='1' channel='2' cell='6' EQon='0' EQtype='0' dblevel='-4.00' freq='12000.00' Q='3.00' />
        	<Bus index='1' channel='3' cell='1' EQon='1' EQtype='0' dblevel='9.00' freq='50.00' Q='3.00' />
        </VoiceMeeterBUSEQ>
        	<PatchComposite c1='0' c2='0' c3='1' mode='0' />
        </VoiceMeeterParameters>
        </VBAudioVoicemeeterSettings>
        """;

    [Fact]
    public void Parses_devices_labels_and_routing()
    {
        var r = VoicemeeterXmlImporter.Parse(Sample);
        var p = r.Profile;

        Assert.True(r.LooksLikeVoicemeeterFile);
        Assert.Empty(r.Warnings);

        Assert.Equal("Mic/Inst/Line In 1/2 (Studio 24c)", p.Strips[0].Device.Name);
        Assert.True(p.Strips[1].Device.IsEmpty);
        Assert.Equal("Headset Microphone (CORSAIR HS8", p.Strips[2].Device.Name);
        Assert.Equal("Speakers (FiiO KA15(2.0))", p.Buses[0].Device.Name);
        Assert.True(p.Buses[1].Device.IsEmpty);
        Assert.Equal("R&D Monitor", p.Buses[2].Device.Name);

        Assert.Equal("MIC", p.Strips[0].Label);
        Assert.Equal("NOT IN USE", p.Strips[1].Label);
        Assert.Equal("", p.Strips[2].Label);
        Assert.Equal("SYSTEM", p.Strips[3].Label);
        Assert.Equal("PHONES", p.Buses[0].Label);
        Assert.Equal("STREAM", p.Buses[3].Label);

        var mic = p.Strips[0];
        Assert.False(mic.Mute);
        Assert.True(mic.Mono);
        Assert.False(mic.A1);
        Assert.True(mic.A2);
        Assert.False(mic.B1);
        Assert.Equal(-3.5, mic.GainDb);

        var system = p.Strips[3];
        Assert.True(system.A1);
        Assert.True(system.A2);
        Assert.False(system.A3);
        Assert.True(system.B1);
        Assert.False(system.B2);
        Assert.Equal(3.0, system.EqLowDb);
        Assert.Equal(-1.5, system.EqMidDb);
        Assert.Equal(0.0, system.EqHighDb);

        Assert.True(p.Strips[1].Mute);
        Assert.Equal(-60, p.Strips[1].GainDb);
    }

    [Fact]
    public void Parses_dynamics()
    {
        var p = VoicemeeterXmlImporter.Parse(Sample).Profile;

        Assert.True(p.Strips[0].Comp.Enabled);
        Assert.Equal(2.5, p.Strips[0].Comp.Ratio);
        Assert.Equal(-40, p.Strips[0].Comp.ThresholdDb);
        Assert.True(p.Strips[0].Comp.AutoMakeup);

        Assert.True(p.Strips[0].Gate.Enabled);
        Assert.Equal(-35, p.Strips[0].Gate.ThresholdDb);
        Assert.Equal(-50, p.Strips[0].Gate.DampingDb);

        Assert.False(p.Strips[1].Comp.Enabled);
        Assert.False(p.Strips[1].Gate.Enabled);
    }

    [Fact]
    public void Parses_buses_and_eq_cells()
    {
        var p = VoicemeeterXmlImporter.Parse(Sample).Profile;

        var a1 = p.Buses[0];
        Assert.True(a1.EqOn);
        Assert.Equal(-6, a1.GainDb);
        Assert.Equal(6, a1.EqLeft.Count);
        Assert.Equal(6, a1.EqRight.Count);

        var cell = a1.EqLeft[0];
        Assert.True(cell.On);
        Assert.Equal(5, cell.Type);
        Assert.Equal(2.5, cell.GainDb);
        Assert.Equal(80, cell.Freq);
        Assert.Equal(0.7, cell.Q);

        var right6 = a1.EqRight[5];
        Assert.False(right6.On);
        Assert.Equal(-4, right6.GainDb);
        Assert.Equal(12000, right6.Freq);

        // Channel 3 is ignored (we only keep L/R).
        Assert.Equal(0, a1.EqLeft[1].GainDb);

        var a2 = p.Buses[1];
        Assert.True(a2.Mute);
        Assert.True(a2.Mono);
        Assert.False(a2.EqOn);
    }

    [Fact]
    public void Virtual_defaults_are_applied()
    {
        var p = VoicemeeterXmlImporter.Parse(Sample).Profile;
        Assert.Equal("Voicemeeter Input", p.Strips[3].Device.Name);
        Assert.Equal("Voicemeeter AUX Input", p.Strips[4].Device.Name);
        Assert.Equal("CABLE Input", p.Buses[3].Device.Name);
        Assert.True(p.Buses[4].Device.IsEmpty);
    }

    [Fact]
    public void Non_voicemeeter_file_is_flagged()
    {
        var r = VoicemeeterXmlImporter.Parse("<Something><Else/></Something>");
        Assert.False(r.LooksLikeVoicemeeterFile);
        Assert.Contains(r.Warnings, w => w.Contains("does not look like"));
    }

    [Fact]
    public void Profile_round_trips_through_json()
    {
        var p = VoicemeeterXmlImporter.Parse(Sample).Profile;
        var copy = ProfileStore.Clone(p);
        Assert.Equal(p.Strips[0].Device.Name, copy.Strips[0].Device.Name);
        Assert.Equal(p.Strips[3].EqLowDb, copy.Strips[3].EqLowDb);
        Assert.Equal(p.Buses[0].EqLeft[0].Freq, copy.Buses[0].EqLeft[0].Freq);
        Assert.Equal(ChannelKind.Virtual, copy.Strips[4].Kind);
    }
}

public class DeviceMatchTests
{
    private static readonly List<(string Id, string Name)> Devices = new()
    {
        ("id-1", "Headset Microphone (CORSAIR HS80 RGB WIRELESS Gaming Receiver)"),
        ("id-2", "Headset Microphone (CORSAIR HS80 MAX)"),
        ("id-3", "Voicemeeter Input (VB-Audio Voicemeeter VAIO)"),
        ("id-4", "Voicemeeter In 1 (VB-Audio Voicemeeter VAIO)"),
        ("id-5", "Voicemeeter AUX Input (VB-Audio Voicemeeter VAIO)"),
    };

    [Fact]
    public void Truncated_name_prefers_shortest_prefix_match()
    {
        int i = DeviceService.Match(Devices, new DeviceRef { Name = "Headset Microphone (CORSAIR HS8" });
        Assert.Equal(1, i);
    }

    [Fact]
    public void Prefix_does_not_confuse_input_with_in_1()
    {
        Assert.Equal(2, DeviceService.Match(Devices, new DeviceRef { Name = "Voicemeeter Input" }));
        Assert.Equal(4, DeviceService.Match(Devices, new DeviceRef { Name = "Voicemeeter AUX Input" }));
    }

    [Fact]
    public void Id_beats_name_and_exact_beats_prefix()
    {
        Assert.Equal(0, DeviceService.Match(Devices, new DeviceRef { Id = "id-1", Name = "Something else" }));
        Assert.Equal(2, DeviceService.Match(Devices, new DeviceRef { Name = "Voicemeeter Input (VB-Audio Voicemeeter VAIO)" }));
        Assert.Equal(-1, DeviceService.Match(Devices, new DeviceRef { Name = "Not here" }));
    }
}
