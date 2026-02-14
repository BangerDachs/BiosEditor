using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;

namespace BiosEditor
{
    public partial class MainWindow : Window
    {
        private byte[] _data;
        private string _loadedPath;

        private AtomTableInfo[] _atomTables = new AtomTableInfo[0];
        private List<ScanCandidate> _candidates = new List<ScanCandidate>();

        private const int BytesPerLine = 16;

        public MainWindow()
        {
            InitializeComponent();
            UpdateUiState();
        }

        private void UpdateUiState()
        {
            bool hasData = _data != null && _data.Length > 0;
            TxtInfo.Text = hasData
                ? $"Datei: {(_loadedPath ?? "(unsaved)")}\nGröße: {_data.Length:N0} Bytes\nLegacyBase: 0x{AtomBios.LegacyBase:X}"
                : "Keine Datei geladen.";

            BtnSetEnabled(hasData);
            TxtStatus.Text = "";
        }

        private void BtnSetEnabled(bool enabled)
        {
            foreach (var btn in FindVisualChildren<Button>(this))
            {
                if (btn.Content != null && (btn.Content.ToString() == "Speichern unter..." || btn.Content.ToString() == "Änderung anwenden"))
                    btn.IsEnabled = enabled;
            }

            TxtJumpOffset.IsEnabled = enabled;
            TxtNew.IsEnabled = enabled;
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj == null) yield break;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
                if (child is T t) yield return t;
                foreach (var childOfChild in FindVisualChildren<T>(child))
                    yield return childOfChild;
            }
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "BIOS Dateien (*.bin;*.rom)|*.bin;*.rom|Alle Dateien (*.*)|*.*",
                Title = "BIOS Datei öffnen"
            };

            if (dlg.ShowDialog() != true) return;

            _loadedPath = dlg.FileName;
            _data = File.ReadAllBytes(dlg.FileName);

            BuildHexView();

            _atomTables = AtomBios.ListDataTables(_data);

            AtomList.ItemsSource = _atomTables;
            AtomList.DisplayMemberPath = nameof(AtomTableInfo.Display);

            ValueList.ItemsSource = null;
            _candidates.Clear();
            TxtValueInfo.Text = "";

            TxtStatus.Text = _atomTables.Length > 0
                ? "ATOM Tabellen gefunden: " + _atomTables.Length + " (klicke Tabelle oder 'Scan Werte')"
                : "Keine ATOM Tabellen gefunden.";

            UpdateUiState();
        }

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;
            if (_atomTables == null || _atomTables.Length == 0)
            {
                TxtStatus.Text = "Keine ATOM Tabellen – Scan nicht möglich.";
                return;
            }

            _candidates = AtomBios.ScanCommonCandidates(_data, _atomTables);

            // Anzeige
            ValueList.ItemsSource = _candidates.Select(c => c.Display).ToList();

            TxtStatus.Text = "Scan fertig: " + _candidates.Count + " Kandidaten.";
            TxtValueInfo.Text = "Tipp: Kandidat anklicken → Jump im Hex. Danach 'Auswahl → Übersetzt' zum Bearbeiten.";
        }

        private void BtnSendToTranslated_Click(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;
            if (_candidates == null || _candidates.Count == 0) return;
            if (ValueList.SelectedIndex < 0 || ValueList.SelectedIndex >= _candidates.Count)
            {
                TxtStatus.Text = "Kein Kandidat ausgewählt.";
                return;
            }

            var c = _candidates[ValueList.SelectedIndex];

            // TranslatedWindow öffnen und Kandidat übernehmen
            var w = new TranslatedWindow(_data);
            w.Owner = this;

            // Offset in TranslatedWindow ist RELATIV zu LegacyBase
            w.AddOrUpdateField(
                c.Name,
                "0x" + c.OffsetRelLegacy.ToString("X"),
                TranslatedWindow.FieldDataType.UInt16,
                1.0,
                c.Value
            );

            w.ShowDialog();

            BuildHexView();
            UpdateUiState();
        }

        private void BtnSaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;

            var dlg = new SaveFileDialog
            {
                Filter = "ROM (*.rom)|*.rom|BIN (*.bin)|*.bin|Alle Dateien (*.*)|*.*",
                Title = "Speichern unter...",
                FileName = _loadedPath != null
                    ? Path.GetFileNameWithoutExtension(_loadedPath) + "_mod" + Path.GetExtension(_loadedPath)
                    : "bios_mod.rom"
            };

            if (dlg.ShowDialog() != true) return;

            File.WriteAllBytes(dlg.FileName, _data);
            TxtStatus.Text = "Gespeichert: " + dlg.FileName;
        }

        private void BuildHexView()
        {
            HexList.Items.Clear();
            if (_data == null) return;

            int lines = (_data.Length + BytesPerLine - 1) / BytesPerLine;

            for (int line = 0; line < lines; line++)
            {
                int offset = line * BytesPerLine;
                int count = Math.Min(BytesPerLine, _data.Length - offset);

                var slice = new byte[count];
                Buffer.BlockCopy(_data, offset, slice, 0, count);

                string hex = string.Join(" ", slice.Select(b => b.ToString("X2")));
                string ascii = new string(slice.Select(b => (b >= 32 && b <= 126) ? (char)b : '.').ToArray());

                string hexPadded = hex.PadRight(BytesPerLine * 3 - 1);

                HexList.Items.Add(new HexLineItem
                {
                    Offset = offset,
                    Text = string.Format("{0:X8}  {1}  |{2}|", offset, hexPadded, ascii)
                });
            }

            HexList.DisplayMemberPath = nameof(HexLineItem.Text);
        }

        private void AtomList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_data == null) return;

            var t = AtomList.SelectedItem as AtomTableInfo;
            if (t == null) return;

            JumpToOffset(t.OffsetAbs);
            TxtStatus.Text = "ATOM Table ausgewählt: " + t.Display;
        }

        private void ValueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_data == null) return;
            if (_candidates == null || _candidates.Count == 0) return;

            int idx = ValueList.SelectedIndex;
            if (idx < 0 || idx >= _candidates.Count) return;

            var c = _candidates[idx];

            JumpToOffset(c.OffsetAbs);

            // Kontext: aktuelle u16 anzeigen
            ushort now;
            if (AtomBios.TryReadUInt16(_data, c.OffsetAbs, out now))
            {
                TxtValueInfo.Text = string.Format(
                    "Auswahl:\n{0}\nAktueller UInt16 @ Abs 0x{1:X} = {2} ({3} {4})\nRel (Legacy) = 0x{5:X}",
                    c.Display, c.OffsetAbs, now, now, c.Unit, c.OffsetRelLegacy
                );
            }
            else
            {
                TxtValueInfo.Text = "Auswahl: " + c.Display;
            }
        }

        private void JumpToOffset(int offset)
        {
            if (_data == null) return;
            if (offset < 0 || offset >= _data.Length) return;

            int line = offset / BytesPerLine;
            if (line < 0 || line >= HexList.Items.Count) return;

            HexList.SelectedIndex = line;
            HexList.ScrollIntoView(HexList.SelectedItem);

            TxtOffset.Text = "0x" + offset.ToString("X");
            TxtOld.Text = _data[offset].ToString("X2");
            TxtNew.Text = TxtOld.Text;
        }

        private void HexList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_data == null) return;

            var line = HexList.SelectedItem as HexLineItem;
            if (line == null) return;

            int offset = line.Offset;
            if (offset >= _data.Length) return;

            TxtOffset.Text = "0x" + offset.ToString("X");
            TxtOld.Text = _data[offset].ToString("X2");
            TxtNew.Text = TxtOld.Text;

            TxtStatus.Text = "Zeile ausgewählt (ändert standardmäßig Byte am Zeilenanfang).";
        }

        private void HexList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox == null) return;

            var scrollViewer = FindVisualChildren<ScrollViewer>(listBox).FirstOrDefault();
            if (scrollViewer == null) return;

            if (e.Delta > 0) scrollViewer.LineUp();
            else scrollViewer.LineDown();

            e.Handled = true;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;

            int offset;
            if (!TryParseHexInt(TxtOffset.Text, out offset))
            {
                TxtStatus.Text = "Offset ungültig.";
                return;
            }

            if (offset < 0 || offset >= _data.Length)
            {
                TxtStatus.Text = "Offset außerhalb der Datei.";
                return;
            }

            byte newVal;
            if (!TryParseHexByte(TxtNew.Text, out newVal))
            {
                TxtStatus.Text = "Neuer Wert ungültig (erlaubt: 00..FF).";
                return;
            }

            byte oldVal = _data[offset];
            _data[offset] = newVal;

            TxtOld.Text = oldVal.ToString("X2");
            TxtStatus.Text = string.Format("Byte geändert @ 0x{0:X}: {1:X2} -> {2:X2}", offset, oldVal, newVal);

            int selectedLine = offset / BytesPerLine;
            BuildHexView();
            if (selectedLine >= 0 && selectedLine < HexList.Items.Count)
                HexList.SelectedIndex = selectedLine;
        }

        private void BtnTranslated_Click(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;

            var window = new TranslatedWindow(_data)
            {
                Owner = this
            };

            window.ShowDialog();

            BuildHexView();
            UpdateUiState();
        }

        private void BtnGo_Click(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;

            int offset;
            if (!TryParseHexInt(TxtJumpOffset.Text, out offset))
            {
                TxtStatus.Text = "Jump Offset ungültig.";
                return;
            }

            if (offset < 0 || offset >= _data.Length)
            {
                TxtStatus.Text = "Jump Offset außerhalb der Datei.";
                return;
            }

            JumpToOffset(offset);
            TxtStatus.Text = string.Format("Gesprungen zu 0x{0:X}.", offset);
        }

        private static bool TryParseHexByte(string s, out byte value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);

            return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseHexInt(string s, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);

            return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private sealed class HexLineItem
        {
            public int Offset { get; set; }
            public string Text { get; set; }
        }
    }
}
