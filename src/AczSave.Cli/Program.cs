using AczSave;

if (args.Length < 2) { Console.Error.WriteLine("usage: AczSave.Cli <save.sav> <steamAccountId> [out.sav]"); return 2; }
var file = File.ReadAllBytes(args[0]);
uint acc = uint.Parse(args[1]);

var raw = new RawSave(Container.Decode(file, acc, out bool outerOk));
Console.WriteLine($"outer checksum {(outerOk ? "ok" : "BAD")}, header magic {(raw.IsValid ? "ok" : "BAD")}, inner checksum {(raw.ChecksumOk ? "ok" : "BAD")}");
Console.WriteLine($"last slot {raw.LastSlot}");
for (int s = 0; s < RawSave.SlotCount; s++)
{
    var e = raw.GetSlotEntry(s);
    var c = raw.GetCampaign(s);
    if (e.IsEmpty || c.IsEmpty) { Console.WriteLine($"slot {s}: empty"); continue; }
    var p = c.Planes;
    int owned = 0; for (int i = 0; i < Campaign.Aircraft.Count; i++) if (p.Owned(i)) owned++;
    Console.WriteLine($"slot {s}: mission {e.Mission} level {e.Level} time {e.Hours}:{e.Minutes:00}:{e.Seconds:00} (record {c.Records.PlayTimeSeconds}s) ASG {c.AceStyleGauge} money {p.Money} owned {owned}/36");
    for (int m = 0; m < Campaign.MissionCount; m++)
        for (int l = 0; l < Campaign.LevelCount; l++)
            for (int st = 0; st < Campaign.StyleCount; st++)
                if (c.Records.Rank(l, m, st) != Campaign.Record.RankNone)
                    Console.WriteLine($"  M{m + 1} L{l} style{st}: rank {c.Records.Rank(l, m, st)} time {c.Records.Time(l, m, st)} score {c.Records.Score(l, m, st)}");
}

var re = Container.Encode(raw.Data, acc);
var back = Container.Decode(re, acc, out bool reOk);
Console.WriteLine($"round trip: {(back.AsSpan().SequenceEqual(raw.Data) && reOk ? "identical" : "MISMATCH")} (file {re.Length} bytes)");
if (args.Length > 2) { File.WriteAllBytes(args[2], re); Console.WriteLine($"wrote {args[2]}"); }
return 0;
