using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Utils;
using MSOutlook = Microsoft.Office.Interop.Outlook;
using MSWord = Microsoft.Office.Interop.Word;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>The email being written in a window: what the writing actions read and where their text goes.</summary>
public sealed partial class OutlookDataService
{
    /// <summary>Unsent email of a compose window, or the inline reply of the main window; null when nothing is being written.</summary>
    public ComposeDraft? GetComposeDraft(object? contextWindow = null)
    {
        EnsureUiThread();
        return WithComposeItem(ResolveWindow(contextWindow), (mail, editor) => new ComposeDraft
        {
            Subject = mail.Subject ?? string.Empty,
            To = mail.To ?? string.Empty,
            Cc = mail.CC ?? string.Empty,
            Body = mail.Body ?? string.Empty,
            Selection = SelectedText(editor)
        });
    }

    /// <summary>
    /// Subject of the email being written, null when nothing is: read at every selection change, so neither the body
    /// nor Word's selection (both can be large or slow) is touched.
    /// </summary>
    public string? GetComposeSubject(object? contextWindow = null)
    {
        EnsureUiThread();
        object? item = null;
        try
        {
            var window = ResolveWindow(contextWindow);
            if (window is MSOutlook.Inspector inspector)
            {
                item = inspector.CurrentItem;
                return item is MSOutlook.MailItem mail && !mail.Sent ? mail.Subject ?? string.Empty : null;
            }
            if (window is MSOutlook.Explorer explorer)
            {
                item = explorer.ActiveInlineResponse;
                return item is MSOutlook.MailItem mail ? mail.Subject ?? string.Empty : null;
            }
            return null;
        }
        catch (COMException ex)
        {
            Infrastructure.Logging.OnlyWorkspaceLog.Logger.Debug(ex, "Email being written not available");
            return null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>Text at the cursor of the email being written (the selection, if any, is replaced as when typing).</summary>
    public bool InsertIntoDraft(string markdown, object? contextWindow = null)
    {
        EnsureUiThread();
        var window = ResolveWindow(contextWindow);
        return window is MSOutlook.Inspector inspector && TryInsertIntoCompose(inspector, markdown)
            || window is MSOutlook.Explorer explorer && TryInsertIntoInlineResponse(explorer, markdown);
    }

    /// <summary>
    /// Replaces the selection the text was written from. False when the selection changed meanwhile (the user went on
    /// writing): the text is not put somewhere else.
    /// </summary>
    public bool ReplaceDraftSelection(string expected, string markdown, object? contextWindow = null)
    {
        EnsureUiThread();
        return WithComposeItem(ResolveWindow(contextWindow), (_, editor) =>
        {
            if (editor == null) return null;
            MSWord.Application? app = null;
            MSWord.Selection? selection = null;
            MSWord.Range? range = null;
            try
            {
                app = editor.Application;
                selection = app?.Selection;
                if (selection == null || selection.Start == selection.End || Normalized(selection.Text) != Normalized(expected)) return null;
                range = selection.Range;
                range.Text = DraftFormatter.ToPlainText(markdown);
                return Done;
            }
            finally
            {
                if (range != null) Marshal.ReleaseComObject(range);
                if (selection != null) Marshal.ReleaseComObject(selection);
                if (app != null) Marshal.ReleaseComObject(app);
            }
        }) != null;
    }

    public bool SetDraftSubject(string subject, object? contextWindow = null)
    {
        EnsureUiThread();
        return WithComposeItem(ResolveWindow(contextWindow), (mail, _) =>
        {
            mail.Subject = subject;
            return Done;
        }) != null;
    }

    /// <summary>A new email with the written text, shown to the user: never sent.</summary>
    public void OpenNewMail(string subject, string bodyMarkdown)
    {
        EnsureUiThread();
        MSOutlook.MailItem? mail = null;
        MSOutlook.Inspector? inspector = null;
        try
        {
            mail = (MSOutlook.MailItem)_application.CreateItem(MSOutlook.OlItemType.olMailItem);
            // Touching GetInspector makes Outlook add the default signature before we edit the body.
            inspector = mail.GetInspector;
            mail.Subject = subject;
            PrependDraft(mail, bodyMarkdown);
            mail.Display(false);
        }
        finally
        {
            if (inspector != null) Marshal.ReleaseComObject(inspector);
            if (mail != null) Marshal.ReleaseComObject(mail);
        }
    }

    private static T? WithComposeItem<T>(object? window, Func<MSOutlook.MailItem, MSWord.Document?, T?> action) where T : class
    {
        object? item = null;
        object? editor = null;
        try
        {
            if (window is MSOutlook.Inspector inspector)
            {
                item = inspector.CurrentItem;
                if (item is not MSOutlook.MailItem mail || mail.Sent) return null;
                editor = inspector.EditorType == MSOutlook.OlEditorType.olEditorWord ? inspector.WordEditor : null;
                return action(mail, editor as MSWord.Document);
            }
            if (window is MSOutlook.Explorer explorer)
            {
                // Null when no inline reply is open in the reading pane.
                item = explorer.ActiveInlineResponse;
                if (item is not MSOutlook.MailItem mail) return null;
                editor = explorer.ActiveInlineResponseWordEditor;
                return action(mail, editor as MSWord.Document);
            }
            return null;
        }
        catch (COMException ex)
        {
            Infrastructure.Logging.OnlyWorkspaceLog.Logger.Debug(ex, "Email being written not available");
            return null;
        }
        finally
        {
            if (editor != null) Marshal.ReleaseComObject(editor);
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    private static string SelectedText(MSWord.Document? editor)
    {
        if (editor == null) return string.Empty;
        MSWord.Application? app = null;
        MSWord.Selection? selection = null;
        try
        {
            // Document.ActiveWindow is null for the inline editor: the selection comes from the application.
            app = editor.Application;
            selection = app?.Selection;
            return selection == null || selection.Start == selection.End ? string.Empty : (selection.Text ?? string.Empty).Trim();
        }
        catch (COMException)
        {
            return string.Empty;
        }
        finally
        {
            if (selection != null) Marshal.ReleaseComObject(selection);
            if (app != null) Marshal.ReleaseComObject(app);
        }
    }

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private static string Normalized(string? text) => Spaces.Replace(text ?? string.Empty, " ").Trim();
}
