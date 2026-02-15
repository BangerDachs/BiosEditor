using System;
using System.Collections.Generic;
using System.Linq;

namespace BiosEditor
{
    // Hilfsklassen und Funktionen zum Lesen einfacher ATOMBIOS-Strukturen.
    public sealed class AtomTableInfo
    {
        public int Index { get; set; }
        public int OffsetRel { get; set; }
        public int OffsetAbs { get; set; }
        public int Size { get; set; }
        public byte FmtRev { get; set; }
        public byte ContentRev { get; set; }

        public string Display
        {
            get
            {
                return string.Format(
                    "[{0}] Rel=0x{1:X}  Abs=0x{2:X}  Size={3}  Rev={4}.{5}",
                    Index, OffsetRel, OffsetAbs, Size, FmtRev, ContentRev
                );
            }
        }
    }

    public sealed class ScanCandidate
    {
        // Ergebnis eines einfachen Scans nach typischen Werten (Power/Voltage/Clock).
        public string Kind { get; set; }           // "Power", "Voltage", "Clock"
        public string Name { get; set; }           // "Power Limit (W) Candidate"
        public int TableIndex { get; set; }        // -1 wenn global
        public int OffsetAbs { get; set; }
        public int OffsetRelLegacy { get; set; }   // relativ zu LegacyBase (0x40000)
        public ushort RawU16 { get; set; }
        public double Value { get; set; }          // ggf. skaliert
        public string Unit { get; set; }           // "W", "mV", "MHz"
        public string Display { get; set; }        // string fürs UI
        public int Score { get; set; }       // je höher, desto wahrscheinlicher
        public int TableSize { get; set; }   // für Anzeige/Debug

    }

    public static class AtomBios
    {
        // Standard-Base-Offset für Legacy-ATOMBIOS.
        public const int LegacyBase = 0x40000;

        public static bool TryGetAtomRomHeaderOffset(byte[] rom, out int atomHdrAbs)
        {
            // Sucht den ATOM ROM Header anhand der "ATOM"-Signatur.
            atomHdrAbs = 0;
            if (rom == null || rom.Length < LegacyBase + 0x200) return false;

            for (int i = LegacyBase; i < Math.Min(rom.Length - 16, LegacyBase + 0x4000); i++)
            {
                if (rom[i] == (byte)'A' && rom[i + 1] == (byte)'T' && rom[i + 2] == (byte)'O' && rom[i + 3] == (byte)'M')
                {
                    int start = i - 4;
                    if (start < 0) continue;

                    ushort size = BitConverter.ToUInt16(rom, start);
                    if (size >= 0x20 && size <= 0x80)
                    {
                        atomHdrAbs = start;
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool TryGetMasterDataTableOffset(byte[] rom, out int masterDataAbs)
        {
            // Ermittelt den Offset der Master Data Table aus dem ROM Header.
            masterDataAbs = 0;

            int atomHdrAbs;
            if (!TryGetAtomRomHeaderOffset(rom, out atomHdrAbs))
                return false;

            int afterSig = atomHdrAbs + 8;
            if (afterSig + 14 * 2 > rom.Length) return false;

            ushort rel = BitConverter.ToUInt16(rom, afterSig + (12 * 2));
            masterDataAbs = LegacyBase + rel;

            return masterDataAbs > 0 && masterDataAbs + 4 <= rom.Length;
        }

        public static AtomTableInfo[] ListDataTables(byte[] rom)
        {
            // Listet alle Table-Einträge aus der Master Data Table.
            if (rom == null || rom.Length == 0) return new AtomTableInfo[0];

            int mdAbs;
            if (!TryGetMasterDataTableOffset(rom, out mdAbs))
                return new AtomTableInfo[0];

            ushort mdSize = BitConverter.ToUInt16(rom, mdAbs);
            if (mdSize < 8) return new AtomTableInfo[0];

            int count = (mdSize - 4) / 2;
            var list = new List<AtomTableInfo>(count);

            for (int i = 0; i < count; i++)
            {
                ushort rel = BitConverter.ToUInt16(rom, mdAbs + 4 + i * 2);
                if (rel == 0) continue;

                int abs = LegacyBase + rel;
                if (abs < 0 || abs + 4 > rom.Length) continue;

                ushort tSize = BitConverter.ToUInt16(rom, abs);
                byte fmt = rom[abs + 2];
                byte content = rom[abs + 3];

                var info = new AtomTableInfo();
                info.Index = i;
                info.OffsetRel = rel;
                info.OffsetAbs = abs;
                info.Size = tSize;
                info.FmtRev = fmt;
                info.ContentRev = content;

                list.Add(info);
            }

            return list.ToArray();
        }

        // --- Low-level scan helpers ---
        public static List<int> FindAllUInt16InRegion(byte[] data, int startAbs, int length, ushort value)
        {
            // Findet alle Positionen eines UInt16-Werts in einem Bereich.
            var results = new List<int>();
            if (data == null || data.Length < 2) return results;
            if (startAbs < 0) startAbs = 0;
            if (startAbs >= data.Length) return results;

            int endAbs = startAbs + length;
            if (endAbs > data.Length) endAbs = data.Length;

            byte lo = (byte)(value & 0xFF);
            byte hi = (byte)((value >> 8) & 0xFF);

            for (int i = startAbs; i < endAbs - 1; i++)
            {
                if (data[i] == lo && data[i + 1] == hi)
                    results.Add(i);
            }

            return results;
        }

        private static void ScanU16Range(byte[] rom, AtomTableInfo[] tables,
            string kind, string unit, int min, int max, int maxPerKind,
            List<ScanCandidate> outList)
        {
            // scannen in ATOM Tabellen
            for (int ti = 0; ti < tables.Length; ti++)
            {
                var t = tables[ti];

                int start = t.OffsetAbs;
                int len = t.Size;

                // Plausibilitäts-Clamp
                if (start < 0 || start >= rom.Length) continue;
                if (len <= 0) continue;
                if (start + len > rom.Length) len = rom.Length - start;

                // Durchlauf als UInt16 little-endian
                for (int i = start; i < start + len - 1; i += 1)
                {
                    ushort v = BitConverter.ToUInt16(rom, i);
                    if (v < min || v > max) continue;

                    // Duplikate vermeiden: gleiche OffsetAbs nur einmal
                    bool exists = false;
                    for (int k = 0; k < outList.Count; k++)
                    {
                        if (outList[k].OffsetAbs == i)
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (exists) continue;

                    var c = new ScanCandidate();
                    c.Kind = kind;
                    c.Unit = unit;
                    c.Name = kind + " Candidate";
                    c.TableIndex = t.Index;
                    c.OffsetAbs = i;
                    c.OffsetRelLegacy = i - LegacyBase;
                    c.RawU16 = v;
                    c.Value = v;

                    c.Display = string.Format(
                        "{0}: {1} {2}   (Rel 0x{3:X} / Abs 0x{4:X})   Table [{5}]",
                        kind, v, unit, c.OffsetRelLegacy, c.OffsetAbs, t.Index
                    );

                    outList.Add(c);

                    // Begrenzung, damit UI nicht überläuft
                    int countKind = 0;
                    for (int kk = 0; kk < outList.Count; kk++)
                        if (outList[kk].Kind == kind) countKind++;

                    if (countKind >= maxPerKind)
                        return;
                }
            }
        }

        public static List<ScanCandidate> ScanCommonCandidates(byte[] rom, AtomTableInfo[] tables)
        {
            // Sucht typische Wertebereiche (Power/Voltage/Clock) als Kandidaten.
            var result = new List<ScanCandidate>();
            if (rom == null || tables == null) return result;

            // Power in Watt: 150..600
            // Voltage in mV: 600..1600
            // Clocks in MHz: 500..4000
            ScanU16Range(rom, tables, "Power (W)", "W", 150, 600, 60, result);
            ScanU16Range(rom, tables, "Voltage (mV)", "mV", 600, 1600, 80, result);
            ScanU16Range(rom, tables, "Clock (MHz)", "MHz", 500, 4000, 120, result);

            return result;
        }

        public static bool TryReadUInt16(byte[] data, int offsetAbs, out ushort value)
        {
            // Liest einen UInt16-Wert an absoluter Position.
            value = 0;
            if (data == null) return false;
            if (offsetAbs < 0 || offsetAbs + 1 >= data.Length) return false;
            value = BitConverter.ToUInt16(data, offsetAbs);
            return true;
        }

        public static bool TryWriteUInt16(byte[] data, int offsetAbs, ushort value)
        {
            // Schreibt einen UInt16-Wert an absolute Position.
            if (data == null) return false;
            if (offsetAbs < 0 || offsetAbs + 1 >= data.Length) return false;
            byte[] b = BitConverter.GetBytes(value);
            Buffer.BlockCopy(b, 0, data, offsetAbs, 2);
            return true;
        }

        public static AtomTableInfo[] GetLargeTables(AtomTableInfo[] tables, int minSize)
        {
            if (tables == null) return new AtomTableInfo[0];

            var list = new List<AtomTableInfo>();
            for (int i = 0; i < tables.Length; i++)
            {
                if (tables[i] != null && tables[i].Size >= minSize)
                    list.Add(tables[i]);
            }
            return list.ToArray();
        }

        private static int ScoreNeighborhoodU16(byte[] rom, int absOffset, int startAbs, int endAbs,
    int min, int max, int windowBytes, int stepBytes)
        {
            // schaut um den Treffer herum, wie viele plausible Werte in einem Fenster liegen
            int score = 0;
            int left = absOffset - windowBytes;
            int right = absOffset + windowBytes;

            if (left < startAbs) left = startAbs;
            if (right > endAbs - 2) right = endAbs - 2;

            for (int i = left; i <= right; i += stepBytes)
            {
                ushort v = BitConverter.ToUInt16(rom, i);
                if (v >= min && v <= max)
                    score++;
            }

            return score;
        }

        public static List<ScanCandidate> ScanCommonCandidatesSmart(byte[] rom, AtomTableInfo[] tables)
        {
            var result = new List<ScanCandidate>();
            if (rom == null || tables == null) return result;

            // 1) Nur große Tabellen -> deutlich weniger Rauschen
            AtomTableInfo[] scanTables = GetLargeTables(tables, 1024);
            if (scanTables.Length == 0) scanTables = tables; // fallback

            // Scan-Parameter
            // Nachbarschaft-Fenster: 64 Bytes links/rechts, Schritt 2 Bytes (u16 aligned)
            const int windowBytes = 64;
            const int stepBytes = 2;

            // Helfer: Kandidaten in einer Tabelle sammeln
            for (int ti = 0; ti < scanTables.Length; ti++)
            {
                AtomTableInfo t = scanTables[ti];
                int start = t.OffsetAbs;
                int len = t.Size;

                if (start < 0 || start >= rom.Length) continue;
                if (len <= 0) continue;
                if (start + len > rom.Length) len = rom.Length - start;

                int end = start + len;

                // Wir laufen byteweise, aber bewerten u16 an jeder Position.
                // (später kann man auf 2er Schritte umstellen, wenn nötig)
                for (int i = start; i < end - 1; i++)
                {
                    ushort v = BitConverter.ToUInt16(rom, i);

                    // 2) Kandidaten-Klassen:
                    // Power: 150..600
                    // Voltage: 600..1600
                    // Clock: 500..4000

                    // POWER
                    if (v >= 150 && v <= 600)
                    {
                        int score = ScoreNeighborhoodU16(rom, i, start, end, 150, 600, windowBytes, stepBytes);

                        // nur brauchbare Treffer
                        if (score >= 3)
                            AddCandidate(result, "Power (W)", "W", t, i, v, score);
                    }

                    // VOLTAGE
                    if (v >= 600 && v <= 1600)
                    {
                        int score = ScoreNeighborhoodU16(rom, i, start, end, 600, 1600, windowBytes, stepBytes);
                        if (score >= 4)
                            AddCandidate(result, "Voltage (mV)", "mV", t, i, v, score);
                    }

                    // CLOCK
                    if (v >= 500 && v <= 4000)
                    {
                        int score = ScoreNeighborhoodU16(rom, i, start, end, 500, 4000, windowBytes, stepBytes);
                        if (score >= 4)
                            AddCandidate(result, "Clock (MHz)", "MHz", t, i, v, score);
                    }
                }
            }

            // 3) Dedupe: gleiche AbsOffsets entfernen
            var dedup = new Dictionary<int, ScanCandidate>();
            for (int i = 0; i < result.Count; i++)
            {
                ScanCandidate c = result[i];
                if (!dedup.ContainsKey(c.OffsetAbs))
                    dedup[c.OffsetAbs] = c;
                else
                {
                    // behalte den höheren Score
                    if (c.Score > dedup[c.OffsetAbs].Score)
                        dedup[c.OffsetAbs] = c;
                }
            }

            var finalList = dedup.Values.ToList();

            // 4) Sortierung: Score desc, dann TableSize desc
            finalList.Sort(delegate (ScanCandidate a, ScanCandidate b)
            {
                int s = b.Score.CompareTo(a.Score);
                if (s != 0) return s;
                return b.TableSize.CompareTo(a.TableSize);
            });

            // 5) optional: harte Begrenzung für UI
            if (finalList.Count > 400)
                finalList = finalList.GetRange(0, 400);

            return finalList;
        }

        private static void AddCandidate(List<ScanCandidate> list, string kind, string unit, AtomTableInfo t, int abs, ushort raw, int score)
        {
            // Skip: zu nah am TableHeader (erste 4 bytes sind size/fmt/content)
            if (abs >= t.OffsetAbs && abs < t.OffsetAbs + 4)
                return;

            ScanCandidate c = new ScanCandidate();
            c.Kind = kind;
            c.Unit = unit;
            c.Name = kind + " Candidate";
            c.TableIndex = t.Index;
            c.OffsetAbs = abs;
            c.OffsetRelLegacy = abs - LegacyBase;
            c.RawU16 = raw;
            c.Value = raw;
            c.Score = score;
            c.TableSize = t.Size;

            c.Display = string.Format(
                "{0}: {1} {2}   Score={3}   (Rel 0x{4:X} / Abs 0x{5:X})   Table [{6}] Size={7}",
                kind, raw, unit, score, c.OffsetRelLegacy, c.OffsetAbs, t.Index, t.Size
            );

            list.Add(c);
        }

        public static List<ScanCandidate> FindPowerLimit304Candidates(byte[] rom, AtomTableInfo[] tables)
        {
            var result = new List<ScanCandidate>();
            if (rom == null || tables == null) return result;

            // 304W = 0x0130 => Bytes 30 01 (Little Endian)
            const ushort target = 304;

            // nur große Tabellen scannen (weniger Rauschen)
            var scanTables = GetLargeTables(tables, 1024);
            if (scanTables.Length == 0) scanTables = tables;

            const int windowBytes = 96; // Nachbarschaft größer, weil Limits oft in Blöcken liegen
            const int stepBytes = 2;    // u16 aligned

            for (int ti = 0; ti < scanTables.Length; ti++)
            {
                var t = scanTables[ti];

                int start = t.OffsetAbs;
                int len = t.Size;

                if (start < 0 || start >= rom.Length) continue;
                if (len <= 0) continue;
                if (start + len > rom.Length) len = rom.Length - start;

                int end = start + len;

                // Treffer (304) suchen
                var hits = FindAllUInt16InRegion(rom, start, len, target);
                for (int h = 0; h < hits.Count; h++)
                {
                    int abs = hits[h];

                    // Score: Wie viele weitere "Power plausible" u16 in der Nähe?
                    int scorePower = ScoreNeighborhoodU16(rom, abs, start, end, 150, 600, windowBytes, stepBytes);

                    // Bonus: wenn in der Nähe auch Voltage und Clock "Cluster" sind, ist es oft ein Limit-Block
                    int scoreVolt = ScoreNeighborhoodU16(rom, abs, start, end, 600, 1600, 64, stepBytes);
                    int scoreClock = ScoreNeighborhoodU16(rom, abs, start, end, 500, 4000, 64, stepBytes);

                    int total = scorePower * 3 + scoreVolt + scoreClock;

                    var c = new ScanCandidate();
                    c.Kind = "PowerLimit 304W";
                    c.Unit = "W";
                    c.Name = "Power Limit (W)";
                    c.TableIndex = t.Index;
                    c.OffsetAbs = abs;
                    c.OffsetRelLegacy = abs - LegacyBase;
                    c.RawU16 = target;
                    c.Value = target;
                    c.Score = total;
                    c.TableSize = t.Size;

                    c.Display = string.Format(
                        "PowerLimit EXACT: {0} W   Score={1} (P{2}/V{3}/C{4})   Rel 0x{5:X} Abs 0x{6:X}   Table[{7}] Size={8}",
                        target, total, scorePower, scoreVolt, scoreClock, c.OffsetRelLegacy, c.OffsetAbs, t.Index, t.Size
                    );

                    result.Add(c);
                }
            }

            // Dedupe (falls gleiche Abs mehrfach)
            var dedup = new Dictionary<int, ScanCandidate>();
            for (int i = 0; i < result.Count; i++)
            {
                var c = result[i];
                if (!dedup.ContainsKey(c.OffsetAbs)) dedup[c.OffsetAbs] = c;
                else if (c.Score > dedup[c.OffsetAbs].Score) dedup[c.OffsetAbs] = c;
            }

            var finalList = new List<ScanCandidate>(dedup.Values);

            // Sort Score desc
            finalList.Sort(delegate (ScanCandidate a, ScanCandidate b)
            {
                return b.Score.CompareTo(a.Score);
            });

            return finalList;
        }

        public static List<int> FindAllUInt32InRegion(byte[] data, int startAbs, int length, uint value)
        {
            var results = new List<int>();
            if (data == null || data.Length < 4) return results;

            if (startAbs < 0) startAbs = 0;
            if (startAbs >= data.Length) return results;

            int endAbs = startAbs + length;
            if (endAbs > data.Length) endAbs = data.Length;

            byte[] p = BitConverter.GetBytes(value); // little endian

            for (int i = startAbs; i < endAbs - 3; i++)
            {
                if (data[i] == p[0] && data[i + 1] == p[1] && data[i + 2] == p[2] && data[i + 3] == p[3])
                    results.Add(i);
            }
            return results;
        }

        public static List<ScanCandidate> FindPowerLimitCandidatesScaled(byte[] rom, AtomTableInfo[] tables, int watts)
        {
            var result = new List<ScanCandidate>();
            if (rom == null || tables == null) return result;

            // nur große Tabellen (weniger Rauschen)
            AtomTableInfo[] scanTables = GetLargeTables(tables, 1024);
            if (scanTables.Length == 0) scanTables = tables;

            // Kandidaten (Wert, Typ, Skalierung, Anzeige)
            // Achtung: wir speichern "Value" immer als Watt, Raw als gespeicherter Wert
            var u16Cases = new (ushort raw, string note)[]
            {
        ((ushort)watts,           "UInt16: W"),
        ((ushort)(watts * 2),     "UInt16: W*2 (0.5W Schritte)"),
        ((ushort)(watts * 4),     "UInt16: W*4 (0.25W Schritte)"),
        ((ushort)(watts * 8),     "UInt16: W*8 (0.125W Schritte)"),
        ((ushort)(watts * 10),    "UInt16: W*10 (0.1W)"),
        ((ushort)(watts * 16),    "UInt16: W*16 (1/16W)"),
            };

            var u32Cases = new (uint raw, string note)[]
            {
        ((uint)watts,                 "UInt32: W"),
        ((uint)(watts * 256),         "UInt32: W*256 (1/256W)"),
        ((uint)(watts * 1000),        "UInt32: mW (W*1000)"),
            };

            for (int ti = 0; ti < scanTables.Length; ti++)
            {
                AtomTableInfo t = scanTables[ti];
                int start = t.OffsetAbs;
                int len = t.Size;
                if (start < 0 || start >= rom.Length) continue;
                if (len <= 0) continue;
                if (start + len > rom.Length) len = rom.Length - start;

                // UInt16 cases
                for (int c = 0; c < u16Cases.Length; c++)
                {
                    var hits = FindAllUInt16InRegion(rom, start, len, u16Cases[c].raw);
                    for (int h = 0; h < hits.Count; h++)
                    {
                        int abs = hits[h];

                        var sc = new ScanCandidate();
                        sc.Kind = "PowerLimit " + watts + "W";
                        sc.Unit = "W";
                        sc.Name = "Power Limit (W)";
                        sc.TableIndex = t.Index;
                        sc.OffsetAbs = abs;
                        sc.OffsetRelLegacy = abs - LegacyBase;
                        sc.RawU16 = u16Cases[c].raw;
                        sc.Value = watts;
                        sc.Score = 0;
                        sc.TableSize = t.Size;

                        sc.Display = string.Format(
                            "Power EXACT {0}W | {1} | Raw={2} (0x{3:X}) | Rel 0x{4:X} Abs 0x{5:X} | Table[{6}] Size={7}",
                            watts, u16Cases[c].note, u16Cases[c].raw, u16Cases[c].raw,
                            sc.OffsetRelLegacy, sc.OffsetAbs, t.Index, t.Size
                        );

                        result.Add(sc);
                    }
                }

                // UInt32 cases
                for (int c = 0; c < u32Cases.Length; c++)
                {
                    var hits = FindAllUInt32InRegion(rom, start, len, u32Cases[c].raw);
                    for (int h = 0; h < hits.Count; h++)
                    {
                        int abs = hits[h];

                        var sc = new ScanCandidate();
                        sc.Kind = "PowerLimit " + watts + "W";
                        sc.Unit = "W";
                        sc.Name = "Power Limit (W)";
                        sc.TableIndex = t.Index;
                        sc.OffsetAbs = abs;
                        sc.OffsetRelLegacy = abs - LegacyBase;
                        sc.RawU16 = 0; // hier ist es u32
                        sc.Value = watts;
                        sc.Score = 0;
                        sc.TableSize = t.Size;

                        sc.Display = string.Format(
                            "Power EXACT {0}W | {1} | RawU32={2} (0x{3:X}) | Rel 0x{4:X} Abs 0x{5:X} | Table[{6}] Size={7}",
                            watts, u32Cases[c].note, u32Cases[c].raw, u32Cases[c].raw,
                            sc.OffsetRelLegacy, sc.OffsetAbs, t.Index, t.Size
                        );

                        result.Add(sc);
                    }
                }
            }

            // Dedupe nach Offset
            var dedup = new Dictionary<int, ScanCandidate>();
            for (int i = 0; i < result.Count; i++)
            {
                var sc = result[i];
                if (!dedup.ContainsKey(sc.OffsetAbs))
                    dedup[sc.OffsetAbs] = sc;
            }

            return new List<ScanCandidate>(dedup.Values);
        }




    }
}
