namespace Nao.Assistant

open System.Threading.Tasks
open Avalonia.Controls

/// Shared UI context — holds the top-level window so deeper views can open native
/// dialogs (e.g. file pickers). Set once during application startup.
module UiContext =
    open System.IO
    open Avalonia.Platform.Storage

    let mutable topLevel: TopLevel option = None

    /// Open a native file picker and read the chosen file as UTF-8 text.
    /// Returns (fileName, content) or None if cancelled / unavailable.
    let pickTextFileAsync () : Task<(string * string) option> =
        task {
            match topLevel with
            | Some tl ->
                let! files =
                    tl.StorageProvider.OpenFilePickerAsync(
                        FilePickerOpenOptions(AllowMultiple = false, Title = "Select a file"))
                if files.Count > 0 then
                    let f = files.[0]
                    use! stream = f.OpenReadAsync()
                    use reader = new StreamReader(stream)
                    let! content = reader.ReadToEndAsync()
                    return Some (f.Name, content)
                else
                    return None
            | None -> return None
        }
