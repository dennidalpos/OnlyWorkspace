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
        ("q01", "Ordine confermato con la {A:Verdemare Logistica S.r.l.}: referente {P:Nicola Basile}.", false),
        ("q02", "{P:Irene Caputo} lavora presso {A:Lumen Digitale} e segue il progetto dal mese scorso.", false),
        ("q03", "Spett.le {A:Cartiera del Serio SpA}, vi inviamo la conferma d'ordine firmata da {P:Valerio Monti}.", false),
        ("q04", "Vi presento {P:Sofia Gentile}, nuova responsabile commerciale di {A:Orizzonte Energia}.", false),
        ("q05", "Fattura emessa da {A:Pasticceria Bellavista S.a.s.} a {P:Carla Fabbri} per la fornitura di dicembre.", false),
        ("q06", "Il tecnico della {A:Termoidraulica Vega} passerà giovedì: chiedere di {P:Enrico Sartori}.", false),
        ("q07", "Dear {P:Olivia Brown}, the meeting with {A:Harbor Lane Consulting} is moved to Friday.", false),
        ("q08", "L'{A:Associazione Amici del Parco} ha invitato {P:Tommaso Leone} e {P:Marta Piras} all'assemblea.", false),
        ("q09", "Pratica seguita dallo {A:Studio Legale Ferri & Partners}, avvocato {P:Michele Ferri}.", false),
        ("q10", "{P:Beatrice Conte} della {A:Nuova Serigrafia Adriatica S.r.l.} chiede un campione del lotto 12.", false),
        ("q11", "Abbiamo ricevuto la candidatura di {P:Simone Orlando} tramite {A:TalentBridge}.", false),
        ("q12", "Il contratto di manutenzione con {A:Ascensori Fenice} scade a giugno, contattare {P:Daniela Costa}.", false),
        ("q13", "Ciao {P:Luigi Mancini}, ti inoltro il listino di {A:Mobilificio Casalini S.n.c.}.", false),
        ("q14", "Consegna presso {A:Ortofrutta Delle Colline}, c.a. {P:Patrizia Vitale}.", false),
        ("q15", "{P:Giorgio Amato} ha firmato per conto della {A:Cooperativa Sociale Primavera}.", false),
        ("q16", "Hello {P:Daniel Wright}, the invoice from {A:Bluebird Analytics Ltd} is attached.", false),
        ("q17", "Il preventivo di {A:Tipografia Moderna} è stato approvato da {P:Roberta Neri}.", false),
        ("q18", "Riunione con {A:Fondazione Casa Serena}: partecipano {P:Alberto Ricci} e {P:Lucia Marini}.", false),
        ("q19", "L'ordine n. 8812 della {A:Calzaturificio Rondine S.p.A.} è in ritardo, ne parlo con {P:Fabio Cattaneo}.", false),
        ("q20", "{P:Elisa Parisi} ci segnala un errore nel listino di {A:Ferramenta Centrale}.", false),
        ("q21", "Abbiamo incontrato {P:Claudio Benedetti} di {A:Vinicola Montecchio} alla fiera di Verona.", false),
        ("q22", "Scrivere a {P:Monica Lombardi}, {A:Banca Territoriale del Piave}, filiale di Treviso.", false),
        ("q23", "Il corriere {A:Rapido Express S.r.l.} ha consegnato il pacco a {P:Gianni Pellegrino}.", false),
        ("q24", "{A:Studio Associato Rossetti} ha trasmesso la dichiarazione di {P:Valentina Sala}.", false),
        ("q25", "Gentile {P:Pietro Fiore}, la {A:Idrotecnica Sud S.r.l.} conferma l'intervento di lunedì.", false),
        ("q26", "La mostra è organizzata da {A:Galleria Palazzo Vecchio} con {P:Cristina Bellini} come curatrice.", false),
        ("q27", "Hi {P:Sarah Johnson}, can you send the quote from {A:Maple Street Printing} again?", false),
        ("q28", "{P:Massimo Rinaldi} è il nuovo amministratore della {A:Immobiliare Colle Verde S.r.l.} e di {A:Servizi Casa}.", false),
        // Held-out slice (#292): written before the OfDisplayName rule was extended, never used to tune it.
        ("q29", "{P:Alessandro Gatti} ha scritto a {A:Veltro} per chiedere una proroga.", false),
        ("q30", "Ordine n. 5521 per {A:Officina Meccanica Tre Ponti}, riferimento {P:Lorenzo Silvestri}.", false),
        ("q31", "La {A:Blu Cielo Viaggi} propone un pacchetto per {P:Anna De Santis} e famiglia.", false),
        ("q32", "Dear {P:Michael Turner}, please confirm the delivery to {A:Greenfield Logistics} by Monday.", false),
        ("q33", "{A:ZETA Informatica S.r.l.} ha aperto il ticket per conto di {P:Giada Ferraro}.", false),
        ("q34", "Il sig. {P:Paolo Fiore} è il titolare di {A:Panificio Sant'Anna}.", false),
        ("q35", "Contattare {P:Gian Luca Bruni} presso {A:Rossi & Figli} per il preventivo.", false),
        ("q36", "{A:Tecnomar} ha vinto la gara; il responsabile è {P:Silvia Pagano}.", false),
        ("q37", "Il corso è tenuto da {P:Riccardo Marchi} per conto di {A:Accademia delle Arti}.", false),
        ("q38", "Hi {P:Laura Evans}, the proposal from {A:Redwood Software} is ready for review.", false),
        ("q39", "{A:Monte Rosa Editori} ringrazia {P:Federico Longhi} per il manoscritto.", false),
        ("q40", "Fattura 2026/77 di {A:Elettroimpianti Dalmasso S.n.c.} intestata a {P:Rita Gallo}.", false),
        ("q41", "Appuntamento con {P:Stefano Leoni} e {P:Marina Zanetti} della {A:Società Agricola La Quercia}.", false),
        ("q42", "Il bonifico a favore di {A:Novaterra} è stato eseguito da {P:Cesare Amoroso}.", false),
        ("q43", "{P:Ilaria Santoro} lavora per {A:Studio 21} e si occupa di grafica.", false),
        ("q44", "Spett.le {A:Libreria Il Faro}, in allegato l'ordine firmato da {P:Giulio Rizzo}.", false),
        ("q45", "La {A:Pro Loco di Valmare} ha invitato {P:Chiara Sorrentino} all'inaugurazione.", false),
        ("q46", "Hello {P:Thomas Baker}, the invoice from {A:Ironwood Partners LLC} is overdue.", false),
        ("q47", "{A:Autofficina Delta} conferma l'intervento di {P:Mauro Bellucci} per mercoledì.", false),
        ("q48", "Il referente di {A:Cantieri Navali Liguri} è {P:Antonella Guidi}.", false),
        ("q49", "Ho inoltrato la richiesta a {A:Solaria} e a {A:Idroservice Nord S.r.l.} tramite {P:Franco Villani}.", false),
        ("q50", "{P:Noemi Carbone} ha prenotato la sala presso {A:Hotel Belvedere}.", false),
        ("q51", "La {A:Casa Editrice Orsa Maggiore} e il {A:Circolo Culturale Aurelio} organizzano l'evento con {P:Davide Moretti}.", false),
        ("q52", "Dear {P:Emma Collins}, thank you for choosing {A:Silverline Travel}.", false),
        ("q53", "{A:Pellicceria Montanari} ha spedito il pacco a {P:Luciana Ferri}.", false),
        ("q54", "{P:Bruno Esposito} ha aperto una pratica con {A:Credito Valdarno S.p.A.}.", false),
        ("q55", "La riunione con {A:Fornaci Riunite} è fissata per giovedì: avvisare {P:Elisabetta Greco}.", false),
        ("q56", "{P:Walter Conti} segnala che {A:Datacore Systems} non risponde dal 3 novembre.", false),
        ("n01", "Buongiorno, confermo la riunione di lunedì alle 10. Porterò il materiale per la presentazione del progetto Aurora.", true),
        ("n02", "Il corso di sicurezza sul lavoro si svolgerà a gennaio e durerà 8 ore. Iscrizioni entro fine mese.", true),
        ("n03", "Promemoria: scadenza del pagamento il 30 novembre, importo 1.250,00 euro. Grazie.", true),
        ("n04", "Ordine ricevuto. La consegna avverrà in 3-5 giorni lavorativi. Per assistenza risponda a questa email.", true),
        ("n05", "Ho aggiornato il foglio di calcolo con i dati di ottobre e il grafico delle vendite per trimestre.", true),
        ("n06", "Vi informiamo che l'ufficio resterà chiuso a Pasqua e il giorno di Natale.", true),
        ("n07", "Allego il verbale dell'assemblea condominiale. Si discute di ascensore, tetto e giardino.", true),
        ("n08", "La nuova versione del software corregge alcuni errori e migliora le prestazioni generali.", true),
        ("n09", "Il progetto Atlante entra nella fase due. Aggiornamento previsto per il 15 dicembre.", true),
        ("n10", "Ricordo che il corso di aggiornamento Excel avanzato si terrà in aula Sirio, secondo piano.", true),
        ("n11", "La riunione del comitato Qualità è spostata a mercoledì; ordine del giorno in allegato.", true),
        ("n12", "Totale ordine 3.480,50 euro, pagamento a 30 giorni data fattura.", true),
        ("n13", "Il servizio Assistenza Clienti risponde dal lunedì al venerdì, dalle 9 alle 18.", true),
        ("n14", "Si prega di compilare il modulo Richiesta Ferie entro il 20 del mese.", true),
        ("n15", "Il piano Marketing 2027 prevede tre campagne: Primavera, Estate e Autunno.", true),
        ("n16", "Nuova release Orion disponibile: correzioni di stabilità e miglioramenti all'interfaccia.", true),
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
