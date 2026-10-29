using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>
/// What the user reads when an operation fails: an Italian sentence with what to do next. Exceptions thrown by the product
/// already carry such a sentence; system ones (English on .NET 10, technical on both runtimes) go to the log, not on screen.
/// </summary>
internal static class ErrorText
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the UAC prompt was refused

    public static string For(Exception ex) => ex switch
    {
        AggregateException { InnerExceptions.Count: 1 } a => For(a.InnerException!),
        Win32Exception { NativeErrorCode: ErrorCancelled } => "Autorizzazione di amministratore annullata: nessuna modifica è stata applicata.",
        Win32Exception => "Windows non ha eseguito l'operazione. Il dettaglio tecnico è nel log.",
        UnauthorizedAccessException => "Accesso negato al file o alla cartella: serve un amministratore di Windows oppure il file è protetto.",
        PathTooLongException or DirectoryNotFoundException or FileNotFoundException => "File o cartella non trovati: controlla il percorso e riprova.",
        IOException => "File in uso o non accessibile: chiudi i programmi che lo usano (Outlook, la tray) e riprova.",
        TimeoutException or TaskCanceledException => "Tempo scaduto: il servizio o il motore dell'archivio non ha risposto in tempo. Riprova tra poco.",
        HttpRequestException or SocketException => "Servizio non raggiungibile: controlla connessione, indirizzo e proxy, poi riprova.",
        CryptographicException => "Dati cifrati non leggibili su questo PC o per questo utente di Windows.",
        JsonException => "Contenuto non valido: il file o la risposta non hanno il formato atteso.",
        _ when IsProductMessage(ex) => ex.Message,
        _ => "Errore imprevisto. Il dettaglio tecnico è nel log (Manutenzione e backup, o la cartella dei log)."
    };

    /// <summary>
    /// Thrown by the product's own code (its type or the method that threw it): those messages are written in Italian for the user.
    /// The same exception types thrown by the framework carry its text, so they are not shown verbatim.
    /// </summary>
    private static bool IsProductMessage(Exception ex) =>
        ex.Message.Length > 0 && (FromProduct(ex.GetType().Assembly.GetName().Name) || FromProduct(ex.TargetSite?.Module.Assembly.GetName().Name));

    private static bool FromProduct(string? assembly) => assembly?.StartsWith("OnlyWorkspace", StringComparison.Ordinal) == true;
}
