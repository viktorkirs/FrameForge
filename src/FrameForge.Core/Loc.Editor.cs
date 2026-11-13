namespace FrameForge.Core;

public static partial class Loc
{
    static Loc()
    {
        (string Key, string English, string German, string Russian)[] entries =
        [
            ("EditVideo", "Edit video", "Video bearbeiten", "Редактировать видео"),
            ("EditorTitle", "Video editor", "Videoeditor", "Видеоредактор"),
            ("CheckUpdates", "Check for updates", "Nach Updates suchen", "Проверить обновления"),
            ("CheckUpdatesOnStartup", "Check for updates on startup", "Beim Start nach Updates suchen", "Проверять обновления при запуске"),
            ("UpdateChecking", "Checking for updates…", "Suche nach Updates…", "Проверка обновлений…"),
            ("UpdateAvailable", "FrameForge {0} is available", "FrameForge {0} ist verfügbar", "Доступна новая версия FrameForge {0}"),
            ("UpdateCurrent", "You have the latest version ({0})", "Die aktuelle Version ({0}) ist installiert", "У вас актуальная версия ({0})"),
            ("UpdateCheckFailed", "Could not check for updates. Try again later.", "Updates konnten nicht geprüft werden. Später erneut versuchen.", "Не удалось проверить обновления. Попробуйте позже."),
            ("UpdateDownload", "Download installer", "Installationsdatei herunterladen", "Скачать установщик"),
            ("UpdateDownloadFailed", "Could not open the download link.", "Der Download-Link konnte nicht geöffnet werden.", "Не удалось открыть ссылку для скачивания."),
            ("EditorOpen", "Open MP4", "MP4 öffnen", "Открыть MP4"),
            ("EditorExport", "Export MP4", "MP4 exportieren", "Экспорт MP4"),
            ("EditorHint", "Click the ruler to seek · Drag blocks to reorder · Drag edges to trim", "Zeitleiste zum Spulen anklicken · Blöcke zum Umordnen ziehen · Ränder zum Kürzen ziehen", "Щёлкните по шкале для перемотки · Перетаскивайте блоки для перестановки · Тяните за края для обрезки"),
            ("EditorLinked", "Video and audio stay linked. Gaps close automatically.", "Video und Ton bleiben verbunden. Lücken werden automatisch geschlossen.", "Видео и звук связаны. Промежутки закрываются автоматически."),
            ("EditorSplit", "Split", "Teilen", "Разрезать"),
            ("EditorDelete", "Delete clip", "Clip löschen", "Удалить блок"),
            ("EditorUndo", "Undo", "Rückgängig", "Отменить"),
            ("EditorRedo", "Redo", "Wiederholen", "Повторить"),
            ("EditorMoveLeft", "Move left", "Nach links", "Сдвинуть влево"),
            ("EditorMoveRight", "Move right", "Nach rechts", "Сдвинуть вправо"),
            ("EditorMoveClip", "Drag to move the linked video and audio clip", "Ziehen, um den verbundenen Video- und Audioclip zu verschieben", "Перетащите, чтобы переместить блок видео вместе со звуком"),
            ("EditorTrimStart", "Drag to trim the start of the clip", "Ziehen, um den Anfang des Clips zu kürzen", "Потяните, чтобы обрезать начало блока"),
            ("EditorTrimEnd", "Drag to trim the end of the clip", "Ziehen, um das Ende des Clips zu kürzen", "Потяните, чтобы обрезать конец блока"),
            ("EditorSeek", "Click or drag to preview a frame", "Klicken oder ziehen, um ein Bild anzuzeigen", "Щёлкните или потяните, чтобы посмотреть кадр"),
            ("EditorIn", "Source in", "Quellanfang", "Начало в исходнике"),
            ("EditorOut", "Source out", "Quellende", "Конец в исходнике"),
            ("EditorApplyTrim", "Apply trim", "Kürzen anwenden", "Применить обрезку"),
            ("EditorLoading", "Opening video…", "Video wird geöffnet…", "Открытие видео…"),
            ("EditorWaveform", "Reading audio waveform…", "Audiowellenform wird gelesen…", "Построение звуковой волны…"),
            ("EditorNoAudio", "No audio in this recording", "Diese Aufnahme enthält keinen Ton", "В этой записи нет звука"),
            ("EditorEmpty", "The timeline is empty. Undo to restore a clip.", "Die Zeitleiste ist leer. Mit Rückgängig einen Clip wiederherstellen.", "Дорожка пуста. Отмените удаление, чтобы вернуть блок."),
            ("EditorProtectSource", "Choose a different file to preserve the original recording.", "Eine andere Datei wählen, um die Originalaufnahme zu erhalten.", "Выберите другой файл, чтобы сохранить исходную запись."),
            ("EditorExportFailed", "Could not export video: {0}", "Video konnte nicht exportiert werden: {0}", "Не удалось экспортировать видео: {0}"),
            ("EditorExporting", "Exporting… {0:0}%", "Exportieren… {0:0}%", "Экспорт… {0:0}%"),
            ("EditorExported", "Saved: {0}", "Gespeichert: {0}", "Сохранено: {0}"),
            ("EditorCanceled", "Export canceled", "Export abgebrochen", "Экспорт отменён"),
            ("EditorTrimInvalid", "Enter times as hh:mm:ss.fff within the original recording; keep at least one frame.", "Zeiten als hh:mm:ss.fff innerhalb der Originalaufnahme eingeben; mindestens ein Bild behalten.", "Укажите время в формате чч:мм:сс.мс в пределах исходной записи; оставьте хотя бы один кадр."),
            ("EditorDiscard", "Close the editor and discard unexported changes?", "Editor schließen und nicht exportierte Änderungen verwerfen?", "Закрыть редактор и сбросить изменения, которые не были экспортированы?"),
            ("EditorZoom", "Zoom", "Zoom", "Масштаб"),
            ("EditorPlay", "Play / pause (Space)", "Wiedergabe / Pause (Leertaste)", "Просмотр / пауза (Пробел)"),
            ("EditorVideoTrack", "VIDEO", "VIDEO", "ВИДЕО"),
            ("EditorAudioTrack", "AUDIO", "AUDIO", "ЗВУК"),
            ("EditorWaveformFailed", "The audio waveform is unavailable. Audio is still included in the export.", "Die Audiowellenform ist nicht verfügbar. Der Ton wird trotzdem exportiert.", "Звуковая волна недоступна. Звук всё равно войдёт в экспорт."),
            ("EditorPlaybackFailed", "Could not play the preview: {0}", "Vorschau konnte nicht abgespielt werden: {0}", "Не удалось воспроизвести предпросмотр: {0}")
        ];
        foreach (var entry in entries)
        {
            En[entry.Key] = entry.English;
            De[entry.Key] = entry.German;
            Ru[entry.Key] = entry.Russian;
        }
    }
}
