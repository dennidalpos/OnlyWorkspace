using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>
/// #292: the archive only knows display names, which were all labelled PERSONA. The local entity index labels a name
/// AZIENDA when, in this order, (1) it carries a legal form or an institution word, (2) it contains "&amp;" or a digit,
/// (3) it is a single word of letters, or (4) one of its words is a generic business noun from <see cref="BusinessWords"/>.
/// Anything else stays PERSONA. A wrong label never unmasks anything: the name is masked either way.
/// </summary>
public static class PrivacyEntityCategory
{
    private static readonly Regex Organization = new(
        @"(?<![\p{L}\p{N}])(s\s?\.?\s?r\s?\.?\s?l\s?\.?(\s?s\.?)?|s\s?\.?\s?p\s?\.?\s?a\s?\.?|s\s?\.?\s?n\s?\.?\s?c\s?\.?|s\s?\.?\s?a\s?\.?\s?s\s?\.?|s\s?\.?\s?c\s?\.?\s?a\s?\.?\s?r\s?\.?\s?l\s?\.?|" +
        @"soc(ieta|ietà)?\.?|cooperativa|coop|fondazione|associazione|banca|studio\s+associato|gruppo|ditta|ltd|llc|inc|gmbh)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static readonly Regex SingleWord = new(@"^\p{L}[\p{L}\-']{2,}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Word = new(@"\p{L}+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>Generic Italian and English business nouns; not derived from any test corpus.</summary>
    internal static readonly HashSet<string> BusinessWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "costruzioni", "edilizia", "impresa", "imprese", "impianti", "impiantistica", "officina", "officine", "meccanica", "carpenteria",
        "fabbrica", "fornaci", "fornace", "stabilimento", "laboratorio", "lavanderia", "panificio", "pasticceria", "macelleria", "pescheria",
        "ortofrutta", "salumificio", "caseificio", "cantina", "vinicola", "azienda", "agricola", "agriturismo", "tipografia", "cartiera",
        "libreria", "cartoleria", "farmacia", "ferramenta", "autofficina", "carrozzeria", "concessionaria", "autotrasporti", "trasporti",
        "logistica", "spedizioni", "corriere", "servizi", "assistenza", "consulenza", "informatica", "sistemi", "tecnologie",
        "telecomunicazioni", "energia", "ambiente", "ecologia", "idraulica", "termoidraulica", "idrotecnica", "serigrafia", "mobilificio",
        "arredamenti", "calzaturificio", "abbigliamento", "gioielleria", "immobiliare", "assicurazioni", "finanziaria", "studio", "studi",
        "ristorante", "trattoria", "pizzeria", "albergo", "hotel", "hostel", "viaggi", "turismo", "editore", "editori", "edizioni",
        "editrice", "galleria", "accademia", "scuola", "istituto", "circolo", "club", "consorzio", "comitato", "ente", "comune", "clinica",
        "ambulatorio", "poliambulatorio", "distribuzione", "commerciale", "industrie", "industria", "manifattura", "produzioni",
        "forniture", "fornitura", "materiali", "ascensori", "elettrodomestici", "noleggio", "sicurezza", "vigilanza", "pulizie",
        "digitale", "comunicazione", "pubblicita", "pubblicità", "agenzia",
        "consulting", "solutions", "systems", "technologies", "technology", "software", "logistics", "travel", "printing", "traders",
        "trading", "analytics", "partners", "partner", "group", "holdings", "industries", "services", "associates", "labs", "works",
        "agency", "media", "digital", "energy", "motors", "foods", "bank",
    };

    public static string OfDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name!.Length > 200) return "PERSONA";
        try
        {
            string trimmed = name.Trim();
            if (Organization.IsMatch(trimmed)) return "AZIENDA";
            if (trimmed.IndexOf('&') >= 0 || ContainsDigit(trimmed)) return "AZIENDA";
            if (SingleWord.IsMatch(trimmed)) return "AZIENDA";
            foreach (Match word in Word.Matches(trimmed))
                if (BusinessWords.Contains(word.Value)) return "AZIENDA";
            return "PERSONA";
        }
        catch (RegexMatchTimeoutException) { return "PERSONA"; }
    }

    private static bool ContainsDigit(string text)
    {
        foreach (char c in text) if (char.IsDigit(c)) return true;
        return false;
    }
}
