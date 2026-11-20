using System.Text;

namespace NerBench;

public sealed record Gold(int Start, int End, string Category);
public sealed record Doc(string Id, string Text, IReadOnlyList<Gold> Spans, bool Negative);

/// <summary>
/// Synthetic corpus: invented people and companies, no real data. Markup: {P:name} = PERSONA, {A:company} = AZIENDA.
/// Negatives hold capitalised words, dates, amounts and project names that must not be masked as PERSONA or AZIENDA.
/// </summary>
public static class Corpus
{
    private static readonly (string Id, string Markup, bool Negative)[] Source =
    {
        ("p01", "Da: {P:Mario Bianchi} <mario.bianchi@esempio.example>\nOggetto: Preventivo cantiere\n\nGentile {P:Laura Verdi}, in allegato il preventivo della {A:Edilsud S.r.l.} per il cantiere di via Roma.", false),
        ("p02", "Buongiorno, sono {P:Giulia Ferrari} della {A:Tecnoservice SpA}. Ci sentiamo domani con {P:Paolo Neri} per chiudere l'ordine.", false),
        ("p03", "Il contratto con {A:Aurora Costruzioni} è stato firmato dal dott. {P:Luca Moretti} il 12 marzo.", false),
        ("p04", "Cordiali saluti,\n{P:Francesca Romano}\nResponsabile acquisti\n{A:Gruppo Alfa Logistica}\nTel. 02 1234567", false),
        ("p05", "L'ing. {P:Andrea Colombo} ha confermato che la {A:Meccanica Brianza} consegnerà i pezzi entro venerdì.", false),
        ("p06", "Ho parlato con {P:Chiara De Luca} dello {A:Studio Associato Conti & Galli}: la pratica è pronta per la firma.", false),
        ("p07", "ho sentito {P:sara conti} della ditta {A:rossi impianti} e conferma la visita di martedì.", false),
        ("p08", "Spett.le {A:Cantina Valdelsa}, alla c.a. di {P:Roberto Esposito}, trasmettiamo l'ordine di fornitura n. 4471.", false),
        ("p09", "{P:Elena Gallo} e {P:Marco Fontana} partecipano al corso organizzato da {A:AreaJob} nel mese di luglio.", false),
        ("p10", "Fattura n. 2026/118 intestata a {A:Pellegrini Forniture S.n.c.}, referente {P:Davide Pellegrini}.", false),
        ("p11", "Hi {P:John Smith}, please find the contract with {A:Northwind Traders} attached. Best regards, {P:Emily Clark}.", false),
        ("p12", "Riunione con {P:Silvia Marchetti}, {P:Antonio Greco} e {P:Federica Bruno} presso {A:Officine Meridionali} giovedì alle 15.", false),
        ("p13", "Il signor {P:Giuseppe Rizzi} ha richiesto un rimborso alla {A:Banca Popolare del Lago} per un bonifico errato.", false),
        ("p14", "Inoltrato da {P:Matteo Villa}\nA: {P:Alessia Sala}\nOggetto: Rinnovo licenze {A:Microsoft}\n\nBuongiorno, vi giro la richiesta del fornitore.", false),
        ("p15", "La {A:Fondazione Il Girasole} ringrazia {P:Anna Maria Longo} per la generosa donazione ricevuta.", false),
        ("p16", "Attestato di frequenza rilasciato da {A:Formazione Nord S.r.l.} a {P:Stefano Barbieri} per il corso di sicurezza sul lavoro.", false),
        ("n01", "Buongiorno, confermo la riunione di lunedì alle 10. Porterò il materiale per la presentazione del progetto Aurora.", true),
        ("n02", "Il corso di sicurezza sul lavoro si svolgerà a gennaio e durerà 8 ore. Iscrizioni entro fine mese.", true),
        ("n03", "Promemoria: scadenza del pagamento il 30 novembre, importo 1.250,00 euro. Grazie.", true),
        ("n04", "Ordine ricevuto. La consegna avverrà in 3-5 giorni lavorativi. Per assistenza risponda a questa email.", true),
        ("n05", "Ho aggiornato il foglio di calcolo con i dati di ottobre e il grafico delle vendite per trimestre.", true),
        ("n06", "Vi informiamo che l'ufficio resterà chiuso a Pasqua e il giorno di Natale.", true),
        ("n07", "Allego il verbale dell'assemblea condominiale. Si discute di ascensore, tetto e giardino.", true),
        ("n08", "La nuova versione del software corregge alcuni errori e migliora le prestazioni generali.", true),
    };

    public static IReadOnlyList<Doc> Load()
    {
        var docs = new List<Doc>();
        foreach (var (id, markup, negative) in Source)
        {
            var text = new StringBuilder(); var spans = new List<Gold>();
            for (int i = 0; i < markup.Length;)
            {
                if (markup[i] == '{' && i + 3 < markup.Length && markup[i + 2] == ':' && (markup[i + 1] == 'P' || markup[i + 1] == 'A'))
                {
                    int close = markup.IndexOf('}', i);
                    int start = text.Length;
                    text.Append(markup, i + 3, close - i - 3);
                    spans.Add(new Gold(start, text.Length, markup[i + 1] == 'P' ? "PERSONA" : "AZIENDA"));
                    i = close + 1;
                }
                else text.Append(markup[i++]);
            }
            docs.Add(new Doc(id, text.ToString(), spans, negative));
        }
        return docs;
    }
}
