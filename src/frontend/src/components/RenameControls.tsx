import { useEffect, useRef, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { renameEvent, renameEventByDateTime, ConflictError, ApiError } from '../api/archiveApi';
import type { EventDetailDto, RenamePreviewDto, RenameRequestDto } from '../types/archive';
import styles from './RenameControls.module.css';

interface RenameControlsProps {
  eventPath: string;
  /** Current display order of file names (from FileList sort state) */
  fileOrder: string[];
  onRenamed: (updated: EventDetailDto) => void;
}

type RenameCommand = { kind: 'sequential'; request: RenameRequestDto } | { kind: 'date' };

export function RenameControls({ eventPath, fileOrder, onRenamed }: RenameControlsProps) {
  const [startNumber, setStartNumber] = useState(1);
  const [error, setError] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState<{ command: RenameCommand; preview: RenamePreviewDto } | null>(null);
  const dialogRef = useRef<HTMLDialogElement>(null);

  const renameMutation = useMutation({
    mutationFn: ({ command, confirmationToken }: { command: RenameCommand; confirmationToken?: string }) =>
      command.kind === 'date'
        ? renameEventByDateTime(eventPath, confirmationToken)
        : renameEvent(eventPath, { ...command.request, confirmationToken }),
    onSuccess: (result, { command }) => {
      if ('filesToDelete' in result) {
        setConfirmation({ command, preview: result });
      } else {
        onRenamed(result);
      }
    },
    onError: (err) => {
      if (err instanceof ConflictError) {
        setError('Umbenennung bereits in Bearbeitung. Bitte warten.');
      } else if (err instanceof ApiError) {
        setError(err.message || `Fehler ${err.status}`);
      } else if (err instanceof Error) {
        setError('Verbindungsfehler: ' + err.message);
      } else {
        setError('Unbekannter Fehler.');
      }
    },
  });

  useEffect(() => {
    const dialog = dialogRef.current;
    if (confirmation && dialog && !dialog.open) dialog.showModal();
    if (!confirmation && dialog?.open) dialog.close();
  }, [confirmation]);

  function run(command: RenameCommand, confirmationToken?: string) {
    setConfirmation(null);
    setError(null);
    renameMutation.mutate({ command, confirmationToken });
  }

  const isBusy = renameMutation.isPending;
  const pendingKind = isBusy ? renameMutation.variables.command.kind : null;

  return (
    <div className={styles.container}>
      <div className={styles.row}>
        <div className={styles.startNumberGroup}>
          <label htmlFor="startNumber" className={styles.label}>
            Startnummer
          </label>
          <input
            id="startNumber"
            type="number"
            min={1}
            max={9999}
            className={styles.numberInput}
            value={startNumber}
            onChange={(e) => setStartNumber(Math.max(1, parseInt(e.target.value, 10) || 1))}
            disabled={isBusy}
          />
        </div>

        <button
          className={[styles.btn, styles.btnPrimary].join(' ')}
          onClick={() => run({ kind: 'sequential', request: {
            startNumber,
            fileOrder: fileOrder.length > 0 ? fileOrder : undefined,
          } })}
          disabled={isBusy}
          title="Dateien fortlaufend umnummerieren (in aktueller Sortierreihenfolge)"
        >
          {pendingKind === 'sequential' ? 'Umbenennen…' : 'Umbenennen'}
        </button>

        <button
          className={[styles.btn, styles.btnSecondary].join(' ')}
          onClick={() => run({ kind: 'date' })}
          disabled={isBusy}
          title="Dateien nach Dateidatum umbenennen"
        >
          {pendingKind === 'date' ? 'Nach Datum…' : 'Nach Datum umbenennen'}
        </button>
      </div>

      <dialog
        ref={dialogRef}
        className={styles.dialog}
        aria-labelledby="dng-confirmation-title"
        aria-describedby="dng-confirmation-warning"
        onCancel={() => setConfirmation(null)}
      >
        {confirmation && (
          <>
            <h2 id="dng-confirmation-title">Löschen beim Umbenennen bestätigen</h2>
            <p id="dng-confirmation-warning">Diese DNG-Dateien werden beim Umbenennen endgültig gelöscht:</p>
            <ul>
              {confirmation.preview.filesToDelete.map((name) => <li key={name}>{name}</li>)}
            </ul>
            <div className={styles.row}>
              <button autoFocus className={`${styles.btn} ${styles.btnSecondary}`} onClick={() => setConfirmation(null)}>
                Abbrechen
              </button>
              <button
                className={`${styles.btn} ${styles.btnDanger}`}
                onClick={() => run(confirmation.command, confirmation.preview.confirmationToken)}
              >
                DNG-Dateien endgültig löschen
              </button>
            </div>
          </>
        )}
      </dialog>

      {error && (
        <div className={styles.errorBanner} role="alert">
          <strong>Fehler:</strong> {error}
          <button
            className={styles.dismissBtn}
            onClick={() => setError(null)}
            aria-label="Fehlermeldung schließen"
          >
            ✕
          </button>
        </div>
      )}
    </div>
  );
}
