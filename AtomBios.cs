using System;
using System.Collections.Generic;

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
    }
}
