import { Toast } from "radix-ui";
import { useCallback, useState, type ReactNode } from "react";
import { SnackbarContext, type SnackbarMessage } from "../lib/snackbar";

type Message = SnackbarMessage & { id: number };

/** Shows short confirmations in the past tense, such as "Link copied" (§16.5). */
export function SnackbarProvider({ children }: { children: ReactNode }) {
  const [messages, setMessages] = useState<Message[]>([]);
  const show = useCallback((text: string, action?: Message["action"]) => {
    setMessages((current) => [...current.slice(-2), { id: Date.now(), text, action }]);
  }, []);
  return (
    <SnackbarContext value={show}>
      <Toast.Provider duration={4000}>
        {children}
        {messages.map((message) => (
          <Toast.Root
            key={message.id}
            onOpenChange={(open) =>
              !open && setMessages((current) => current.filter((m) => m.id !== message.id))
            }
            className="flex min-h-12 items-center gap-2 rounded-xs bg-on-surface py-1 pr-2 pl-4 text-body-md text-surface shadow-elevation-2"
          >
            <Toast.Description className="flex-1 py-2">{message.text}</Toast.Description>
            {message.action && (
              <Toast.Action
                altText={message.action.label}
                onClick={message.action.onClick}
                className="h-control cursor-pointer rounded-full px-3 text-label-lg text-primary-container hover:bg-surface/10"
              >
                {message.action.label}
              </Toast.Action>
            )}
          </Toast.Root>
        ))}
        <Toast.Viewport className="fixed inset-x-4 bottom-24 z-50 m-0 flex list-none flex-col items-center gap-2 p-0 outline-none sm:bottom-6 sm:left-1/2 sm:w-auto sm:-translate-x-1/2 md:bottom-6 [&>li]:w-full sm:[&>li]:w-auto sm:[&>li]:min-w-80" />
      </Toast.Provider>
    </SnackbarContext>
  );
}
