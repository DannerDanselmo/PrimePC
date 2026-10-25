using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using PrimePC.Diagnostics;
using PrimePC.GpuTesting;
using PrimePC.Sensors;
using PrimePC.Testing;

namespace PrimePC.Reports;

/// <summary>Gera o relatório de diagnóstico em PDF (A4).</summary>
public static class PdfReportExporter
{
    private const string Font = "Segoe UI";
    private static readonly Color Accent = new(0x0E, 0x76, 0x8C);
    private static readonly Color HeaderFill = new(0xE6, 0xF2, 0xF5);
    private static readonly Color Muted = new(0x5F, 0x6B, 0x72);
    private static readonly object FontInit = new();
    private static bool _fontsReady;

    public static void Export(DiagnosticReport report, string path)
    {
        EnsureFonts();
        var doc = Build(report);
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }

    private static void EnsureFonts()
    {
        lock (FontInit)
        {
            if (_fontsReady)
                return;
            GlobalFontSettings.FontResolver ??= new WindowsFontResolver();
            _fontsReady = true;
        }
    }

    private static Document Build(DiagnosticReport r)
    {
        var c = CultureInfo.CurrentCulture;
        var doc = new Document();
        doc.Info.Title = "Relatório de diagnóstico — PrimePC";
        doc.Info.Author = "PrimePC";

        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = 9;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(3);

        var h1 = doc.Styles[StyleNames.Heading1]!;
        h1.Font.Size = 14;
        h1.Font.Bold = true;
        h1.Font.Color = Accent;
        h1.ParagraphFormat.SpaceBefore = Unit.FromPoint(14);
        h1.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        h1.ParagraphFormat.KeepWithNext = true;

        var h2 = doc.Styles[StyleNames.Heading2]!;
        h2.Font.Size = 11;
        h2.Font.Bold = true;
        h2.ParagraphFormat.SpaceBefore = Unit.FromPoint(8);
        h2.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
        h2.ParagraphFormat.KeepWithNext = true;

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);

        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7.5;
        footer.Format.Font.Color = Muted;
        footer.AddText($"PrimePC {r.AppVersion} · gerado em {r.GeneratedAt:dd/MM/yyyy HH:mm} · página ");
        footer.AddPageField();
        footer.AddText(" de ");
        footer.AddNumPagesField();

        var title = section.AddParagraph("Relatório de diagnóstico do computador");
        title.Format.Font.Size = 18;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = Accent;
        var sub = section.AddParagraph(
            $"Sessão iniciada em {r.SessionStart:dd/MM/yyyy HH:mm:ss} · duração {SensorUnits.FormatDuration(r.SessionDuration)} · " +
            $"intervalo de leitura {r.UpdateIntervalSeconds.ToString("0.##", c)} s");
        sub.Format.Font.Color = Muted;
        section.AddParagraph(r.AccessLevel).Format.Font.Color = Muted;

        if (r.Inventory is { } inv)
        {
            section.AddParagraph("Especificações", StyleNames.Heading1);
            KeyValueTable(section, new[]
            {
                ("Processador", $"{inv.Cpu.Name} — {inv.Cpu.Cores} núcleos / {inv.Cpu.Threads} threads"),
                ("Clock máximo informado", inv.Cpu.MaxClockMHz is { } mhz ? $"{mhz} MHz" : "Não disponível"),
                ("Cache L2 / L3", $"{Kb(inv.Cpu.L2CacheKB)} / {Kb(inv.Cpu.L3CacheKB)}"),
                ("Placa-mãe", $"{inv.Board.Manufacturer} {inv.Board.Product}"),
                ("BIOS/UEFI", $"{inv.Board.BiosVersion ?? "?"} ({inv.Board.BiosDate:dd/MM/yyyy}) · {inv.Board.FirmwareMode}"),
                ("Memória", $"{SensorUnits.FormatBytes(inv.Memory.InstalledBytes)} instalada · {inv.Memory.Modules.Count} módulo(s) · {inv.Memory.ChannelEstimate}"),
                ("Sistema", $"{inv.Os.Caption} {inv.Os.DisplayVersion} (build {inv.Os.Build}) {inv.Os.Architecture}"),
            }.Concat(inv.Os.ComputerName is { } name ? new[] { ("Nome do computador", name) } : Array.Empty<(string, string)>()));

            if (inv.Memory.Modules.Count > 0)
            {
                section.AddParagraph("Módulos de memória", StyleNames.Heading2);
                var t = Table(section, new[] { 3.0, 2.0, 2.2, 2.4, 3.0, 4.8 },
                    "Slot", "Capacidade", "Tipo", "Velocidade", "Fabricante", "Modelo");
                foreach (var m in inv.Memory.Modules)
                {
                    Row(t, m.Slot, SensorUnits.FormatBytes(m.CapacityBytes), m.MemoryType,
                        m.ConfiguredSpeedMTs is { } s ? $"{s} MT/s" : "—", m.Manufacturer ?? "—", m.PartNumber ?? "—");
                }

                Note(section, inv.Memory.XmpExpoStatus);
            }

            if (inv.Gpus.Count > 0)
            {
                section.AddParagraph("Placas de vídeo", StyleNames.Heading2);
                var t = Table(section, new[] { 6.0, 3.0, 3.6, 4.8 }, "Modelo", "Memória dedicada", "Driver", "Modo de vídeo");
                foreach (var g in inv.Gpus)
                {
                    Row(t, g.Name, g.DedicatedMemoryBytes is { } vram ? SensorUnits.FormatBytes(vram) : "Não disponível",
                        g.DriverVersion ?? "—", g.CurrentMode ?? "—");
                }
            }

            if (inv.Disks.Count > 0)
            {
                section.AddParagraph("Armazenamento", StyleNames.Heading2);
                var t = Table(section, new[] { 5.6, 2.4, 2.6, 2.4, 4.4 }, "Modelo", "Capacidade", "Tipo", "Saúde", "Volumes (livre)");
                foreach (var d in inv.Disks)
                {
                    Row(t, d.Model, SensorUnits.FormatBytes(d.SizeBytes), $"{d.MediaType} {d.BusType}", d.HealthStatus ?? "Não disponível",
                        string.Join(", ", d.Volumes.Select(v => $"{v.Letter} {SensorUnits.FormatBytes(v.FreeBytes)}")));
                }
            }
        }

        if (r.Bottleneck is { } b)
        {
            section.AddParagraph("Análise de gargalo", StyleNames.Heading1);
            if (r.BottleneckSessionName is not null)
                Note(section, r.BottleneckSessionName);
            var headline = section.AddParagraph(b.Headline);
            headline.Format.Font.Size = 11;
            headline.Format.Font.Bold = true;
            section.AddParagraph(b.Explanation);
            if (b.Frames is { } fr)
            {
                section.AddParagraph("Fluidez (FPS medido a cada quadro)", StyleNames.Heading2);
                Bullet(section, $"{fr.Game ?? "Jogo"} · {fr.Api} · {fr.Frames:N0} quadros");
                Bullet(section, $"FPS médio {fr.AverageFps:0} · 1% low {(fr.Low1Fps is { } l1 ? l1.ToString("0") : "—")} · " +
                                $"0,1% low {(fr.Low01Fps is { } l01 ? l01.ToString("0") : "—")} · pior quadro {fr.MaxFrameTimeMs:0} ms");
                if (b.SmoothnessDetail is not null)
                    Bullet(section, b.SmoothnessDetail);
            }

            foreach (var f in b.Findings)
            {
                var p = section.AddParagraph();
                p.AddFormattedText($"{LikelihoodText.Display(f.Likelihood)} — {f.Title}", TextFormat.Bold);
                foreach (var e in f.Evidence)
                    Bullet(section, e);
            }

            section.AddParagraph("Condições", StyleNames.Heading2);
            foreach (var cnd in b.Conditions)
                Bullet(section, cnd);
            section.AddParagraph("Sugestões", StyleNames.Heading2);
            foreach (var s in b.Suggestions)
                Bullet(section, s);
            section.AddParagraph("Limitações da análise", StyleNames.Heading2);
            foreach (var l in b.Limitations)
                Bullet(section, l);
        }

        if (r.RamTest is { } ram)
        {
            section.AddParagraph("Teste de memória RAM", StyleNames.Heading1);
            var res = section.AddParagraph(ram.Summary);
            res.Format.Font.Bold = true;
            KeyValueTable(section, new[]
            {
                ("Data", $"{ram.StartedAt:dd/MM/yyyy HH:mm}"),
                ("Modo", ram.Mode switch { MemoryTestMode.Quick => "Rápido", MemoryTestMode.Full => "Completo", _ => "Por duração" }),
                ("Memória testada", $"{SensorUnits.FormatBytes(ram.BytesTested)} de {SensorUnits.FormatBytes(ram.TotalPhysicalBytes)}"),
                ("Passagens / padrões", $"{ram.PassesCompleted} / {ram.PatternsCompleted}"),
                ("Duração", SensorUnits.FormatDuration(ram.Elapsed)),
                ("Erros", ram.ErrorCount.ToString(c)),
            });
            if (ram.Errors.Count > 0)
            {
                var t = Table(section, new[] { 2.2, 1.4, 4.2, 3.4, 3.4, 2.8 }, "Hora", "Passagem", "Padrão", "Esperado", "Lido", "Deslocamento");
                foreach (var e in ram.Errors.Take(40))
                    Row(t, e.Time.ToString("HH:mm:ss"), e.Pass.ToString(c), e.Pattern, $"0x{e.Expected:X16}", $"0x{e.Actual:X16}", $"0x{e.Offset:X}");
            }

            foreach (var n in ram.Notes)
                Bullet(section, n);
        }

        if (r.VramTest is { } vramTest)
        {
            section.AddParagraph("Teste de VRAM (memória da placa de vídeo)", StyleNames.Heading1);
            var res = section.AddParagraph(vramTest.Summary);
            res.Format.Font.Bold = true;
            KeyValueTable(section, new[]
            {
                ("Placa de vídeo", vramTest.DeviceName),
                ("Data", $"{vramTest.StartedAt:dd/MM/yyyy HH:mm}"),
                ("VRAM testada", $"{SensorUnits.FormatBytes(vramTest.BytesTested)} de {SensorUnits.FormatBytes(vramTest.DeviceMemoryBytes)}"),
                ("Passagens / padrões", $"{vramTest.PassesCompleted} / {vramTest.PatternsCompleted}"),
                ("Duração", SensorUnits.FormatDuration(vramTest.Elapsed)),
                ("Erros", vramTest.ErrorCount.ToString(c)),
            });
            if (vramTest.Errors.Count > 0)
            {
                var t = Table(section, new[] { 1.6, 5.0, 3.4, 3.2, 3.2 }, "Passagem", "Padrão", "Deslocamento", "Esperado", "Lido");
                foreach (var e in vramTest.Errors.Take(40))
                    Row(t, e.Pass.ToString(c), e.Pattern, $"0x{e.Offset:X}", $"0x{e.Expected:X8}", $"0x{e.Actual:X8}");
            }

            foreach (var n in vramTest.Notes)
                Bullet(section, n);
        }

        if (r.CpuStress.Count > 0 || r.Benchmarks.Count > 0 || r.GpuStress.Count > 0)
        {
            section.AddParagraph("Estabilidade e benchmarks", StyleNames.Heading1);
            foreach (var s in r.CpuStress)
            {
                var p = section.AddParagraph();
                p.AddFormattedText($"Teste de CPU ({s.StartedAt:dd/MM HH:mm}, {s.Threads} threads): ", TextFormat.Bold);
                p.AddText(s.Summary + (s.MaxTemperature is { } mt ? $" Temperatura máxima: {mt:0.0} °C." : ""));
            }

            foreach (var g in r.GpuStress)
            {
                var p = section.AddParagraph();
                p.AddFormattedText($"Teste de GPU ({g.DeviceName}, {g.StartedAt:dd/MM HH:mm}): ", TextFormat.Bold);
                p.AddText($"{g.Summary} Desempenho médio estimado: {g.AverageGflops.ToString("N0", c)} GFLOPS." +
                          (g.MaxTemperature is { } gt ? $" Temperatura máxima: {gt:0.0} °C." : ""));
            }

            if (r.Benchmarks.Count > 0)
            {
                var t = Table(section, new[] { 5.4, 2.8, 2.2, 2.6, 4.4 }, "Benchmark", "Resultado", "Variação", "Data", "Condições");
                foreach (var bm in r.Benchmarks)
                {
                    Row(t, bm.Name, $"{bm.Value.ToString("0.##", c)} {bm.Unit}", $"±{bm.SpreadPercent.ToString("0.#", c)}%",
                        bm.At.ToString("dd/MM HH:mm"), bm.Conditions);
                }
            }
        }

        if (r.StorageBenchmarks.Count > 0)
        {
            section.AddParagraph("Desempenho de armazenamento (teste não destrutivo)", StyleNames.Heading1);
            var t = Table(section, new[] { 3.6, 2.4, 2.4, 2.8, 2.8, 3.4 }, "Volume", "Seq. leitura", "Seq. gravação", "4K leitura", "4K gravação", "Data");
            foreach (var s in r.StorageBenchmarks)
            {
                Row(t, $"{s.Volume} {s.DiskModel}", $"{s.SequentialReadMBs:0} MB/s", $"{s.SequentialWriteMBs:0} MB/s",
                    $"{s.Random4kReadIops:0} IOPS", $"{s.Random4kWriteIops:0} IOPS", s.At.ToString("dd/MM HH:mm"));
            }
        }

        if (r.Drivers is { } drv)
        {
            section.AddParagraph("Drivers", StyleNames.Heading1);
            KeyValueTable(section, new[]
            {
                ("Drivers instalados", $"{drv.Total} ({drv.ThirdParty} de fabricantes, {drv.Total - drv.ThirdParty} incluídos no Windows)"),
                ("Com problema / atenção", $"{drv.Problems} / {drv.Warnings}"),
                ("Sem assinatura digital", drv.Unsigned.ToString(c)),
                ("Windows Update", drv.UpdateStatus),
                ("Lista lida em", drv.CollectedAt.ToString("dd/MM/yyyy HH:mm")),
            });
            if (drv.Video.Count > 0)
            {
                section.AddParagraph("Drivers de vídeo", StyleNames.Heading2);
                var t = Table(section, new[] { 6.0, 4.0, 4.6, 2.8 }, "Dispositivo", "Fabricante", "Versão", "Data");
                foreach (var v in drv.Video)
                    Row(t, v.Device, v.Provider, v.Version, v.Date);
            }

            if (drv.Attention.Count > 0)
            {
                section.AddParagraph("Itens que precisam de atenção", StyleNames.Heading2);
                var t = Table(section, new[] { 5.0, 2.6, 9.8 }, "Dispositivo", "Estado", "Detalhe");
                foreach (var a in drv.Attention)
                    Row(t, a.Device, a.Status, a.Detail);
            }
            else
            {
                Note(section, "Nenhum dispositivo com problema e nenhuma atualização pendente encontrada.");
            }
        }

        if (r.Alerts.Count > 0)
        {
            section.AddParagraph("Alertas disparados", StyleNames.Heading1);
            var t = Table(section, new[] { 2.6, 14.8 }, "Hora", "Alerta");
            foreach (var a in r.Alerts.TakeLast(60))
                Row(t, a.Time.ToString("dd/MM HH:mm:ss"), a.Message);
        }

        if (r.Sensors.Count > 0)
        {
            section.AddParagraph("Sensores detectados (mínimo / média / máximo da sessão)", StyleNames.Heading1);
            foreach (var group in r.Sensors.GroupBy(s => s.Hardware))
            {
                section.AddParagraph(group.Key, StyleNames.Heading2);
                var t = Table(section, new[] { 5.6, 2.8, 2.8, 2.8, 2.8, 0.6 }, "Sensor", "Mínimo", "Média", "Máximo", "Tipo", "");
                foreach (var s in group)
                {
                    string flag = s.CriticalSamples > 0 ? "!" : s.WarningSamples > 0 ? "·" : "";
                    Row(t, s.Name, s.MinText, s.AverageText, s.MaxText, s.Kind, flag);
                }
            }

            Note(section, "\"!\" = houve leituras em estado crítico; \"·\" = houve leituras em estado de atenção. " +
                          "\"Não disponível\" = o hardware, driver ou permissão não forneceu leitura válida.");
        }

        section.AddParagraph("Limitações", StyleNames.Heading1);
        foreach (var l in r.Limitations)
            Bullet(section, l);
        if (r.Inventory?.Notes is { Count: > 0 } notes)
        {
            foreach (var n in notes)
                Bullet(section, n);
        }

        return doc;
    }

    private static string Kb(int? kb) => kb is null ? "?" : SensorUnits.FormatBytes(kb.Value * 1024L);

    private static void KeyValueTable(Section section, IEnumerable<(string Key, string Value)> rows)
    {
        var t = section.AddTable();
        t.Borders.Width = 0;
        t.AddColumn(Unit.FromCentimeter(4.4));
        t.AddColumn(Unit.FromCentimeter(13.0));
        foreach (var (k, v) in rows)
        {
            var row = t.AddRow();
            row.TopPadding = Unit.FromPoint(1.5);
            row.BottomPadding = Unit.FromPoint(1.5);
            var key = row.Cells[0].AddParagraph(k);
            key.Format.Font.Color = Muted;
            row.Cells[1].AddParagraph(v);
        }

        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(2);
    }

    private static Table Table(Section section, double[] widthsCm, params string[] headers)
    {
        var t = section.AddTable();
        t.Borders.Width = 0.4;
        t.Borders.Color = Colors.LightGray;
        t.Format.Font.Size = 8;
        foreach (var w in widthsCm)
            t.AddColumn(Unit.FromCentimeter(w));
        var header = t.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = HeaderFill;
        header.Format.Font.Bold = true;
        for (int i = 0; i < headers.Length; i++)
            header.Cells[i].AddParagraph(headers[i]);
        return t;
    }

    private static void Row(Table t, params string[] values)
    {
        var row = t.AddRow();
        row.TopPadding = Unit.FromPoint(1);
        row.BottomPadding = Unit.FromPoint(1);
        for (int i = 0; i < values.Length; i++)
            row.Cells[i].AddParagraph(values[i]);
    }

    private static void Bullet(Section section, string text)
    {
        var p = section.AddParagraph("• " + text);
        p.Format.LeftIndent = Unit.FromCentimeter(0.4);
        p.Format.FirstLineIndent = Unit.FromCentimeter(-0.3);
    }

    private static void Note(Section section, string text)
    {
        var p = section.AddParagraph(text);
        p.Format.Font.Size = 8;
        p.Format.Font.Color = Muted;
    }
}
