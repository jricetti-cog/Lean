import { useEffect, useRef } from 'react';
import { basicSetup, EditorView } from 'codemirror';
import { Annotation, Compartment, EditorState } from '@codemirror/state';
import { StreamLanguage } from '@codemirror/language';
import { csharp } from '@codemirror/legacy-modes/mode/clike';
import { python } from '@codemirror/lang-python';

/** Marks `value` prop syncs so they are not reported back through `onChange` as user edits. */
const external = Annotation.define<boolean>();

const languageOf = (language: 'CSharp' | 'Python') => (language === 'Python' ? python() : StreamLanguage.define(csharp));

/** CodeMirror 6 editor with C# / Python highlighting. */
export function CodeEditor({ value, language, onChange }: { value: string; language: 'CSharp' | 'Python'; onChange: (v: string) => void }) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const lang = useRef(new Compartment());
  const changed = useRef(onChange);
  changed.current = onChange;

  useEffect(() => {
    const v = new EditorView({
      parent: host.current!,
      state: EditorState.create({
        doc: value,
        extensions: [
          basicSetup,
          lang.current.of(languageOf(language)),
          EditorView.updateListener.of((u) => {
            if (u.docChanged && !u.transactions.some((t) => t.annotation(external))) changed.current(u.state.doc.toString());
          }),
          EditorView.theme({ '&': { height: '100%' }, '.cm-scroller': { fontFamily: 'var(--mono)' } }),
        ],
      }),
    });
    view.current = v;
    return () => v.destroy();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    view.current?.dispatch({ effects: lang.current.reconfigure(languageOf(language)) });
  }, [language]);

  useEffect(() => {
    const v = view.current;
    if (v && v.state.doc.toString() !== value) v.dispatch({ changes: { from: 0, to: v.state.doc.length, insert: value }, annotations: external.of(true) });
  }, [value]);

  return <div className="editor" ref={host} data-testid="code-editor" />;
}
