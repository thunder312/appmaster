using System.Diagnostics;
using System.Windows.Media;
using Appmaster.Models;
using Appmaster.Services;
using EulenwerkCI;

namespace Appmaster.ViewModels;

/// <summary>Anzeige-Hülle um eine Ressource: aktueller Zustand und welche laufenden Apps sie nutzen.</summary>
public sealed class RessourceVm : Beobachtbar
{
    public Ressource Modell { get; }
    public string RechnerName { get; set; } = "";

    private Zustand _zustand = Zustand.Unbekannt;
    private string _nutzer = "";
    private bool _beschaeftigt;

    public RessourceVm(Ressource modell) => Modell = modell;

    public string Name => Modell.Name;
    public string Beschreibung => Modell.Beschreibung;
    public bool KannSteuern => Modell.Steuerung != Steuerung.NurUeberwachen;

    public Zustand Zustand
    {
        get => _zustand;
        set
        {
            if (!Setze(ref _zustand, value)) return;
            Melden(nameof(ZustandText)); Melden(nameof(ZustandName)); Melden(nameof(KannStarten)); Melden(nameof(KannStoppen));
        }
    }

    public string ZustandName => _zustand.ToString();
    public string ZustandText => _zustand switch
    {
        Zustand.Laeuft => "Läuft",
        Zustand.Gestoppt => "Nicht erreichbar",
        Zustand.Startet => "Startet …",
        _ => "Unbekannt",
    };

    /// <summary>Namen der laufenden Apps, die diese Ressource gerade brauchen.</summary>
    public string Nutzer
    {
        get => _nutzer;
        set { if (Setze(ref _nutzer, value)) { Melden(nameof(NutzerText)); Melden(nameof(IstBenutzt)); } }
    }

    public bool IstBenutzt => _nutzer.Length > 0;
    public string NutzerText => IstBenutzt ? $"Genutzt von {_nutzer}" : "Wird nicht genutzt";

    public bool Beschaeftigt
    {
        get => _beschaeftigt;
        set { if (Setze(ref _beschaeftigt, value)) { Melden(nameof(KannStarten)); Melden(nameof(KannStoppen)); } }
    }

    /// <summary>Ist alles eingetragen, was zum Starten/Stoppen nötig ist (Befehl bzw. Container)?</summary>
    public bool StartMoeglich => Modell.Steuerung switch
    {
        Steuerung.Befehl => !string.IsNullOrWhiteSpace(Modell.StartBefehl),
        Steuerung.Agent => !string.IsNullOrWhiteSpace(Modell.AgentContainer),
        _ => false,
    };
    public bool StopMoeglich => Modell.Steuerung switch
    {
        Steuerung.Befehl => !string.IsNullOrWhiteSpace(Modell.StopBefehl),
        Steuerung.Agent => !string.IsNullOrWhiteSpace(Modell.AgentContainer),
        _ => false,
    };
    public bool KannStarten => StartMoeglich && !_beschaeftigt && _zustand != Zustand.Laeuft;
    public bool KannStoppen => StopMoeglich && !_beschaeftigt && _zustand == Zustand.Laeuft;

    public void ModellGeaendert()
    {
        Melden(nameof(Name)); Melden(nameof(Beschreibung)); Melden(nameof(KannSteuern));
        Melden(nameof(KannStarten)); Melden(nameof(KannStoppen));
    }
}

public enum RechnerZustand { Unbekannt, Online, Offline, Startet }

/// <summary>Anzeige-Hülle um einen Rechner: erreichbar? Messwerte vom Agent, Aufwecken/Herunterfahren.</summary>
public sealed class RechnerVm : Beobachtbar
{
    public Rechner Modell { get; }
    private RechnerZustand _zustand = RechnerZustand.Unbekannt;
    private AgentStatus? _daten;
    private bool _beschaeftigt;
    private string _meldung = "";

    public RechnerVm(Rechner modell) => Modell = modell;

    public string Name => Modell.Name;
    public string Adresse => Modell.IstLokal ? "dieser PC" : Modell.Adresse;

    public RechnerZustand Zustand
    {
        get => _zustand;
        set
        {
            if (!Setze(ref _zustand, value)) return;
            Melden(nameof(ZustandName)); Melden(nameof(ZustandText)); Melden(nameof(KannAufwecken)); Melden(nameof(KannHerunterfahren));
        }
    }

    // Gleiche Namen wie bei der Ressource, damit der Statuspunkt-Stil wiederverwendet werden kann
    public string ZustandName => _zustand switch
    {
        RechnerZustand.Online => "Laeuft", RechnerZustand.Offline => "Gestoppt", RechnerZustand.Startet => "Startet", _ => "Unbekannt",
    };

    public string ZustandText => _zustand switch
    {
        RechnerZustand.Online => "Online", RechnerZustand.Offline => "Aus / nicht erreichbar",
        RechnerZustand.Startet => "Wird aufgeweckt …", _ => "Unbekannt",
    };

    public string Meldung { get => _meldung; set => Setze(ref _meldung, value); }

    public bool Beschaeftigt
    {
        get => _beschaeftigt;
        set { if (Setze(ref _beschaeftigt, value)) { Melden(nameof(KannAufwecken)); Melden(nameof(KannHerunterfahren)); } }
    }

    public bool KannAufwecken => !_beschaeftigt && _zustand != RechnerZustand.Online && !Modell.IstLokal && WakeOnLan.MacGueltig(Modell.MacAdresse);
    public bool KannHerunterfahren => !_beschaeftigt && _zustand == RechnerZustand.Online && Modell.AgentArt == AgentArt.LinuxAgent && (_daten?.KannHerunterfahren ?? false);

    public AgentStatus? Daten
    {
        get => _daten;
        set
        {
            _daten = value;
            foreach (var n in new[] { nameof(HatMessdaten), nameof(CpuProzent), nameof(CpuText), nameof(RamProzent), nameof(RamText), nameof(HatGpu),
                                      nameof(GpuProzent), nameof(GpuText), nameof(InfoText), nameof(KannHerunterfahren) })
                Melden(n);
        }
    }

    public bool HatMessdaten => _daten != null;
    public bool HatCpu => _daten?.Cpu != null;
    public double CpuProzent => _daten?.Cpu ?? 0;
    public string CpuText => _daten?.Cpu is { } c ? $"CPU {c:0} %" : "CPU –";
    public double RamProzent => _daten is { RamGesamt: > 0, RamBelegt: not null } d ? d.RamBelegt.Value / d.RamGesamt.Value * 100 : 0;
    public string RamText => _daten is { RamGesamt: > 0, RamBelegt: not null } d ? $"RAM {d.RamBelegt / 1024:0.0} / {d.RamGesamt / 1024:0.0} GB" : "RAM –";

    private GpuInfo? Gpu => _daten?.Gpus?.FirstOrDefault();
    public bool HatGpu => Gpu != null;
    public double GpuProzent => Gpu?.Auslastung ?? 0;
    public string GpuText
    {
        get
        {
            if (Gpu is not { } g) return "GPU –";
            // Bei Chips mit gemeinsamem Speicher (iGPU) ist der GTT-Wert aussagekräftiger als der kleine VRAM-Block.
            var (b, t) = g.GttGesamt > (g.VramGesamt ?? 0) ? (g.GttBelegt, g.GttGesamt) : (g.VramBelegt, g.VramGesamt);
            var speicher = t > 0 && b != null ? $" · {b / 1024:0.0} / {t / 1024:0.0} GB" : "";
            return $"GPU {g.Auslastung:0} %{speicher}";
        }
    }

    public string InfoText
    {
        get
        {
            if (_daten == null) return "";
            var teile = new List<string>();
            if (_daten.Temperaturen is { Count: > 0 } t) teile.Add($"{t.Values.Max():0} °C");
            if (_daten.Laufzeit is { } s) { var z = TimeSpan.FromSeconds(s); teile.Add(z.TotalDays >= 1 ? $"läuft seit {z.Days} T {z.Hours} Std" : $"läuft seit {z.Hours} Std {z.Minutes} Min"); }
            if (_daten.Last is { Length: > 0 } l) teile.Add($"Last {l[0]:0.00}");
            if (_daten.Platten is { Count: > 0 } p) teile.Add(string.Join(", ", p.Select(d => $"{d.Pfad} {d.Belegt:0}/{d.Gesamt:0} GB")));
            if (_daten.Container is { Count: > 0 } c) teile.Add($"Container {c.Count(x => x.Zustand == "running")}/{c.Count} aktiv");
            if (_daten.NurLesen) teile.Add("nur lesend");
            return string.Join(" · ", teile);
        }
    }

    public void ModellGeaendert()
    {
        Melden(nameof(Name)); Melden(nameof(Adresse)); Melden(nameof(KannAufwecken)); Melden(nameof(KannHerunterfahren));
    }
}

/// <summary>Anzeige-Hülle um eine App: läuft sie, welcher Prozess gehört ihr, in welcher Kategoriefarbe erscheint sie.</summary>
public sealed class AppVm : Beobachtbar
{
    public AppEintrag Modell { get; }
    public Process? Prozess { get; set; }

    private bool _laeuft;
    private string _status = "";
    private bool _beschaeftigt;

    public AppVm(AppEintrag modell)
    {
        Modell = modell;
        EulenwerkTheme.Geaendert += _ => Melden(nameof(KategoriePinsel));
    }

    public string Name => Modell.Name;
    public string Beschreibung => Modell.Beschreibung;
    public string KategorieName => KategorieFarben.Anzeige(Modell.Kategorie);
    public string TypText => Modell.Typ == AppTyp.Web ? "Web" : "Windows";
    public Brush KategoriePinsel => KategorieFarben.Pinsel(Modell.Kategorie, EulenwerkTheme.IstHell);

    public bool Laeuft
    {
        get => _laeuft;
        set { if (Setze(ref _laeuft, value)) { Melden(nameof(KannStarten)); Melden(nameof(KannBeenden)); Melden(nameof(StatusAnzeige)); } }
    }

    public bool Beschaeftigt
    {
        get => _beschaeftigt;
        set { if (Setze(ref _beschaeftigt, value)) { Melden(nameof(KannStarten)); Melden(nameof(KannBeenden)); } }
    }

    /// <summary>Freitext zum aktuellen Schritt ("Starte Ollama …", "Wartet auf FLUX …").</summary>
    public string Status
    {
        get => _status;
        set { if (Setze(ref _status, value)) Melden(nameof(StatusAnzeige)); }
    }

    public string StatusAnzeige => _status.Length > 0 ? _status : _laeuft ? "Läuft" : "";
    public bool KannStarten => !_laeuft && !_beschaeftigt;
    public bool KannBeenden => _laeuft && !_beschaeftigt;

    public string RessourcenText { get; set; } = "";

    public void ModellGeaendert()
    {
        Melden(nameof(Name)); Melden(nameof(Beschreibung)); Melden(nameof(KategorieName)); Melden(nameof(KategoriePinsel));
        Melden(nameof(TypText)); Melden(nameof(RessourcenText));
    }
}

/// <summary>Akzentfarben je Kategorie (aus dem Eulenwerk-Design-System, dunkel/hell).</summary>
public static class KategorieFarben
{
    private static readonly Dictionary<Kategorie, (string Dunkel, string Hell)> Farben = new()
    {
        [Kategorie.Organisation] = ("#5B8DEF", "#2F6FE0"),
        [Kategorie.Kreativ] = ("#3ECF8E", "#16A06A"),
        [Kategorie.Medien] = ("#A78BFA", "#7C5CE6"),
        [Kategorie.Technik] = ("#F0B429", "#D99400"),
        [Kategorie.Spielerei] = ("#F472B6", "#D9468F"),
        [Kategorie.Alltag] = ("#E8735A", "#D2583D"),
        [Kategorie.Administration] = ("#2BC4D9", "#0E9BB0"),
    };

    public static string Anzeige(Kategorie k) => k switch
    {
        Kategorie.Technik => "Haus & Technik",
        _ => k.ToString(),
    };

    public static Brush Pinsel(Kategorie k, bool hell)
    {
        var (d, h) = Farben[k];
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hell ? h : d));
        b.Freeze();
        return b;
    }
}
