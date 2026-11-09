using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DSPiConsole.Core.Link;
using DSPiConsole.Usb;

namespace DSPiConsole.Core.Tests.Link;

/// <summary>The DSPi Link command authorization table (spec 6.3, 9.3 and 10
/// rule 7). The coverage tests read the opcode lists from the app and, when it
/// is on this machine, the firmware, so adding an opcode without classifying
/// it fails here. Ported from the macOS Console's LinkPolicyTests.</summary>
public class LinkPolicyTests
{
    private static LinkPolicy Policy => LinkPolicy.Embedded ?? throw new InvalidOperationException("commands.json is not embedded");

    [Fact]
    public void TheEmbeddedTableLoads()
    {
        var policy = Policy;
        // Recorded as the table says. dspi-link 86f10e3 still labels it "1.1"
        // though it carries 1.2's rows (0x03, 0x54-0x57), so don't pin a value.
        Assert.NotEmpty(policy.SpecVersion);
        Assert.NotEmpty(policy.Generated);
        Assert.True(policy.Entries.Count > 200, "the table should cover the whole vendor surface");

        // One entry per code, sorted, none with an empty direction list.
        int previous = -1;
        foreach (var entry in policy.Entries)
        {
            Assert.True(entry.Code > previous, $"{entry.Name}: entries must be unique and sorted by code");
            previous = entry.Code;
            Assert.NotEmpty(entry.Dirs);
            Assert.NotEmpty(entry.Name);
        }

        // 0x01 and 0x02 are the MS OS 2.0 and WebUSB platform requests.
        Assert.Null(policy.Entry(0x01));
        Assert.Null(policy.Entry(0x02));
    }

    // ── Coverage ──

    [Fact]
    public void EveryVendorCommandIsInTheTable()
    {
        var codes = typeof(VendorCommands).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(byte))
            .Select(f => (f.Name, Code: (byte)f.GetRawConstantValue()!))
            .ToList();
        Assert.True(codes.Count > 200, "VendorCommands should list the whole vendor surface");
        var missing = codes.Where(c => Policy.Entry(c.Code) == null).Select(c => $"{c.Name} (0x{c.Code:X2})").ToList();
        Assert.True(missing.Count == 0, "VendorCommands absent from commands.json: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryFirmwareRequestCodeIsInTheTable()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Projects", "DSPi-Firmware", "firmware", "DSPi", "config.h");
        if (!File.Exists(path)) return; // firmware not on this machine
        var source = File.ReadAllText(path, Encoding.UTF8);
        var matches = Regex.Matches(source, @"#define\s+(REQ_[A-Z0-9_]+)\s+(0x[0-9A-Fa-f]+|\d+)\b");
        Assert.True(matches.Count > 200, "failed to parse config.h; the pattern has drifted");
        var missing = new List<string>();
        foreach (Match m in matches)
        {
            var text = m.Groups[2].Value;
            int value = text.StartsWith("0x") ? Convert.ToInt32(text[2..], 16) : int.Parse(text);
            if (value is < 0 or > 255) continue;
            if (Policy.Entry((byte)value) == null) missing.Add($"{m.Groups[1].Value} (0x{value:X2})");
        }
        Assert.True(missing.Count == 0, "firmware REQ_ codes absent from commands.json: " + string.Join(", ", missing));
    }

    // ── Classification ──

    [Fact]
    public void AnUnknownCodeIsConfig()
    {
        Assert.Null(Policy.Entry(0x00));
        Assert.Equal(LinkCommandClass.Config, Policy.Classify(0x00, LinkDirection.Set));
        Assert.Equal(LinkCommandClass.Config, Policy.Classify(0x00, LinkDirection.Get));
    }

    /// <summary>GET_STATUS is read on IN, but the firmware has no OUT handler;
    /// an unexpected direction fails safe rather than inheriting the class.</summary>
    [Fact]
    public void ADirectionTheTableDoesNotListIsConfig()
    {
        Assert.Equal(LinkCommandClass.Read, Policy.Classify(0x50, LinkDirection.Get));
        Assert.Equal(LinkCommandClass.Config, Policy.Classify(0x50, LinkDirection.Set));
    }

    [Theory]
    [InlineData(0xF0, LinkDirection.Get, LinkCommandClass.Config)] // ENTER_BOOTLOADER: write-as-read
    [InlineData(0x53, LinkDirection.Get, LinkCommandClass.Config)] // FACTORY_RESET: write-as-read
    [InlineData(0xD2, LinkDirection.Set, LinkCommandClass.Control)]
    [InlineData(0x50, LinkDirection.Get, LinkCommandClass.Read)]
    [InlineData(0xA0, LinkDirection.Get, LinkCommandClass.Read)]
    [InlineData(0xA1, LinkDirection.Set, LinkCommandClass.Control)]
    [InlineData(0x7C, LinkDirection.Get, LinkCommandClass.Config)]
    [InlineData(0x42, LinkDirection.Set, LinkCommandClass.Control)]
    [InlineData(0x43, LinkDirection.Get, LinkCommandClass.Read)]
    [InlineData(0x3E, LinkDirection.Set, LinkCommandClass.Control)] // SET_TUBE_PARAM
    [InlineData(0x3F, LinkDirection.Get, LinkCommandClass.Read)]    // GET_TUBE_PARAM
    [InlineData(0x82, LinkDirection.Get, LinkCommandClass.Read)]    // GET_CLOCK_DIAG
    [InlineData(0x82, LinkDirection.Set, LinkCommandClass.Config)]
    // Spec 1.2: device names and input source names replace the retired rows.
    [InlineData(0x56, LinkDirection.Set, LinkCommandClass.Control)] // SET_DEVICE_NAME, like device.rename
    [InlineData(0x57, LinkDirection.Get, LinkCommandClass.Read)]    // GET_DEVICE_NAME
    [InlineData(0x54, LinkDirection.Set, LinkCommandClass.Control)] // input source name
    [InlineData(0x55, LinkDirection.Get, LinkCommandClass.Read)]
    public void SpotChecks(byte code, LinkDirection direction, LinkCommandClass expected) =>
        Assert.Equal(expected, Policy.Classify(code, direction));

    /// <summary>Selecting a latency option writes flash and restarts the
    /// outputs, so it is config; reading it is open to viewers.</summary>
    [Fact]
    public void OutputLatencyIsClassedPerDirection()
    {
        Assert.Equal(LinkCommandClass.Config, Policy.Classify(0x03, LinkDirection.Set));
        Assert.Equal(LinkCommandClass.Read, Policy.Classify(0x03, LinkDirection.Get));
        Assert.False(Policy.IsAllowed(LinkRole.Control, 0x03, LinkDirection.Set));
        Assert.True(Policy.IsAllowed(LinkRole.Viewer, 0x03, LinkDirection.Get));
    }

    /// <summary>REQ_LIMITER is one opcode both ways: a viewer may poll its
    /// meter but not change a setting.</summary>
    [Fact]
    public void TheLimiterIsClassedPerDirection()
    {
        Assert.Equal(LinkCommandClass.Control, Policy.Classify(0x81, LinkDirection.Set));
        Assert.Equal(LinkCommandClass.Read, Policy.Classify(0x81, LinkDirection.Get));
        Assert.True(Policy.IsAllowed(LinkRole.Viewer, 0x81, LinkDirection.Get));
        Assert.False(Policy.IsAllowed(LinkRole.Viewer, 0x81, LinkDirection.Set));
        Assert.True(Policy.IsAllowed(LinkRole.Control, 0x81, LinkDirection.Set));

        var viewerDenied = Policy.Denied(LinkRole.Viewer);
        Assert.Contains((0x81, LinkDirection.Set), viewerDenied.Select(d => ((int)d.Code, d.Direction)));
        Assert.DoesNotContain((0x81, LinkDirection.Get), viewerDenied.Select(d => ((int)d.Code, d.Direction)));
    }

    /// <summary>class must be the strictest of a row's classes, so a 1.0 hub
    /// that ignores the key fails safe; classes may only name listed directions.</summary>
    [Fact]
    public void PerDirectionRowsKeepTheStrictestClass()
    {
        foreach (var entry in Policy.Entries)
        {
            if (entry.Classes is { } classes)
            {
                Assert.Equal(entry.Dirs.OrderBy(d => d), classes.Keys.OrderBy(k => k));
                var strictest = entry.Dirs.Select(d => entry.ClassFor(LinkPolicy.ParseDirection(d)!.Value)).Max();
                Assert.Equal(strictest, entry.Class);
            }
            // A SET is never read, whatever the row says for its GET.
            if (entry.Accepts(LinkDirection.Set))
                Assert.NotEqual(LinkCommandClass.Read, entry.ClassFor(LinkDirection.Set));
        }
    }

    [Fact]
    public void ARowWithoutClassesAppliesItsClassToBothDirections()
    {
        var policy = LinkPolicy.Parse(Encoding.UTF8.GetBytes("""
            {"spec_version": "1.1", "generated": "x", "commands": [
              {"code": 129, "name": "LIMITER", "dirs": ["set", "get"], "class": "control"},
              {"code": 130, "name": "SPLIT", "dirs": ["set", "get"], "class": "control",
               "classes": {"set": "control", "get": "read"}}
            ]}
            """));
        Assert.Equal(LinkCommandClass.Control, policy.Classify(129, LinkDirection.Get));
        Assert.Equal(LinkCommandClass.Read, policy.Classify(130, LinkDirection.Get));
        Assert.Equal(LinkCommandClass.Control, policy.Classify(130, LinkDirection.Set));
    }

    /// <summary>Every write-as-read command is classified by what it does, not
    /// its direction (spec 6.3): the ones commands.md section 1.1 names.</summary>
    [Fact]
    public void WriteAsReadCommandsAreNeverRead()
    {
        byte[] writeAsRead = [0x51, 0x52, 0x53, 0x7C, 0x83, 0x90, 0x91, 0x92, 0xB1, 0xB3,
                              0xC0, 0xC2, 0xC4, 0xC6, 0xC8, 0xD6, 0xE4, 0xEC, 0xF0, 0xF1];
        foreach (var code in writeAsRead)
            Assert.True(Policy.Classify(code, LinkDirection.Get) != LinkCommandClass.Read, $"0x{code:X2} mutates and must not be read");
    }

    // ── Roles ──

    [Fact]
    public void RolePermissions()
    {
        Assert.False(Policy.IsAllowed(LinkRole.Viewer, 0xD2, LinkDirection.Set));
        Assert.True(Policy.IsAllowed(LinkRole.Viewer, 0xD3, LinkDirection.Get));
        Assert.True(Policy.IsAllowed(LinkRole.Control, 0xD2, LinkDirection.Set));
        Assert.False(Policy.IsAllowed(LinkRole.Control, 0xF0, LinkDirection.Get));

        foreach (var entry in Policy.Entries)
            foreach (var dir in entry.Dirs)
                Assert.True(Policy.IsAllowed(LinkRole.Admin, entry.Code, LinkPolicy.ParseDirection(dir)!.Value), entry.Name);
        // Even an unlisted code is admin-allowed, since unlisted means config.
        Assert.True(Policy.IsAllowed(LinkRole.Admin, 0x00, LinkDirection.Set));
    }

    [Fact]
    public void DeniedLists()
    {
        var viewerDenied = Policy.Denied(LinkRole.Viewer);
        Assert.NotEmpty(viewerDenied);
        foreach (var (code, direction) in viewerDenied)
            Assert.NotEqual(LinkCommandClass.Read, Policy.Classify(code, direction));
        // Everything not denied is readable by a viewer.
        var denied = viewerDenied.ToHashSet();
        foreach (var entry in Policy.Entries)
            foreach (var d in entry.Dirs.Select(x => LinkPolicy.ParseDirection(x)!.Value))
                if (entry.ClassFor(d) == LinkCommandClass.Read) Assert.DoesNotContain((entry.Code, d), denied);

        Assert.Empty(Policy.Denied(LinkRole.Admin));

        var controlDenied = Policy.Denied(LinkRole.Control);
        foreach (var (code, direction) in controlDenied)
            Assert.Equal(LinkCommandClass.Config, Policy.Classify(code, direction));
        Assert.Contains(controlDenied, d => d.Code == 0xF0);
    }

    /// <summary>A hub with no table at all permits only admins.</summary>
    [Fact]
    public void TheEmptyTableFailsSafe()
    {
        Assert.False(LinkPolicy.Empty.IsAllowed(LinkRole.Control, 0xD2, LinkDirection.Set));
        Assert.False(LinkPolicy.Empty.IsAllowed(LinkRole.Viewer, 0x50, LinkDirection.Get));
        Assert.True(LinkPolicy.Empty.IsAllowed(LinkRole.Admin, 0xD2, LinkDirection.Set));
    }
}
