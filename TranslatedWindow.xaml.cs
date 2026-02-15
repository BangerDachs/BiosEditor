using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;

namespace BiosEditor
{
    public partial class TranslatedWindow : Window
    {
        private readonly byte[] _data;

        // Offsets im UI sollen RELATIV zum Legacy-VBIOS sein (ab 0x40000)
        private const int LegacyBase = AtomBios.LegacyBase;

        public ObservableCollection<TranslatedField> Fields { get; } = new ObservableCollection<TranslatedField>();
        public Array DataTypes { get; } = Enum.GetValues(typeof(FieldDataType));

        public TranslatedWindow(byte[] data)
        {
            _data = data;
            InitializeComponent();
            DataContext = this;
            TypeColumn.ItemsSource = DataTypes;

            // Wichtig: diese Offsets sind nur Platzhalter und jetzt RELATIV zum LegacyBase!
            // Beispiel: "0x34C" wäre abs 0x40000+0x34C
            Fields.Add(new TranslatedField("Power Limit (W)", "0x0", FieldDataType.UInt16, 1));
            Fields.Add(new TranslatedField("Core Clock (MHz)", "0x0", FieldDataType.UInt16, 1));
            Fields.Add(new TranslatedField("Memory Clock (MHz)", "0x0", FieldDataType.UInt16, 1));
            Fields.Add(new TranslatedField("Voltage (mV)", "0x0", FieldDataType.UInt16, 1));
        }

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            // Liest Werte aus dem RAM-Buffer anhand der Offsets.
            int updated = 0;

            foreach (var field in Fields)
            {
                if (!TryParseHexInt(field.OffsetHex, out int relOffset))
                    continue;

                int absOffset = LegacyBase + relOffset;

                if (!TryReadUInt(_data, absOffset, field.DataType, out uint raw))
                    continue;

                double scale = field.Scale <= 0 ? 1 : field.Scale;
                field.Value = raw * scale;
                updated++;
            }

            TranslatedGrid.Items.Refresh();
            TxtStatus.Text = updated > 0
                ? "Werte geladen (Offsets relativ zum Legacy-VBIOS / 0x40000)."
                : "Keine Werte geladen (Offsets prüfen).";
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            // Schreibt die geänderten Werte zurück in den RAM-Buffer.
            int updated = 0;

            foreach (var field in Fields)
            {
                if (!TryParseHexInt(field.OffsetHex, out int relOffset))
                    continue;

                int absOffset = LegacyBase + relOffset;

                double scale = field.Scale <= 0 ? 1 : field.Scale;
                uint raw = (uint)Math.Max(0, Math.Round(field.Value / scale));

                if (!TryWriteUInt(_data, absOffset, field.DataType, raw))
                    continue;

                updated++;
            }

            TxtStatus.Text = updated > 0
                ? "Werte geschrieben (in RAM-Buffer)."
                : "Keine Werte geschrieben (Offsets prüfen).";
        }

        private static bool TryReadUInt(byte[] data, int offset, FieldDataType dataType, out uint value)
        {
            // Liest abhängig vom Datentyp Byte/UInt16/UInt32.
            value = 0;
            if (data == null) return false;
            if (offset < 0 || offset >= data.Length) return false;

            switch (dataType)
            {
                case FieldDataType.Byte:
                    value = data[offset];
                    return true;

                case FieldDataType.UInt16:
                    if (offset + 1 >= data.Length) return false;
                    value = BitConverter.ToUInt16(data, offset);
                    return true;

                case FieldDataType.UInt32:
                    if (offset + 3 >= data.Length) return false;
                    value = BitConverter.ToUInt32(data, offset);
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryWriteUInt(byte[] data, int offset, FieldDataType dataType, uint value)
        {
            // Schreibt abhängig vom Datentyp Byte/UInt16/UInt32.
            if (data == null) return false;
            if (offset < 0 || offset >= data.Length) return false;

            switch (dataType)
            {
                case FieldDataType.Byte:
                    data[offset] = (byte)value;
                    return true;

                case FieldDataType.UInt16:
                    if (offset + 1 >= data.Length) return false;
                    var u16 = BitConverter.GetBytes((ushort)value);
                    Buffer.BlockCopy(u16, 0, data, offset, u16.Length);
                    return true;

                case FieldDataType.UInt32:
                    if (offset + 3 >= data.Length) return false;
                    var u32 = BitConverter.GetBytes(value);
                    Buffer.BlockCopy(u32, 0, data, offset, u32.Length);
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseHexInt(string s, out int value)
        {
            // Hex-String (z. B. "0x1A2") in Int umwandeln.
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);

            return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        public enum FieldDataType
        {
            // Datentypen, die im UI auswählbar sind.
            Byte,
            UInt16,
            UInt32
        }

        public sealed class TranslatedField
        {
            public TranslatedField(string name, string offsetHex, FieldDataType dataType, double scale)
            {
                Name = name;
                OffsetHex = offsetHex;
                DataType = dataType;
                Scale = scale;
            }

            public string Name { get; set; }
            public string OffsetHex { get; set; }     // RELATIV zu 0x40000
            public FieldDataType DataType { get; set; }
            public double Scale { get; set; }
            public double Value { get; set; }
        }

        public void AddOrUpdateField(string name, string offsetHex, FieldDataType dataType, double scale, double initialValue)
        {
            // Fügt einen Eintrag hinzu oder aktualisiert ihn, falls er bereits existiert.
            // existiert schon?
            for (int i = 0; i < Fields.Count; i++)
            {
                if (string.Equals(Fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    Fields[i].OffsetHex = offsetHex;
                    Fields[i].DataType = dataType;
                    Fields[i].Scale = scale;
                    Fields[i].Value = initialValue;
                    TranslatedGrid.Items.Refresh();
                    return;
                }
            }

            // neu
            var f = new TranslatedField(name, offsetHex, dataType, scale);
            f.Value = initialValue;
            Fields.Add(f);
            TranslatedGrid.Items.Refresh();
        }

    }
}
