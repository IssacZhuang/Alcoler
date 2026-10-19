import { useEffect } from "react";
import { init } from "./controller";
import { ChatPane } from "./components/ChatPane";
import { ModelModal } from "./components/ModelModal";
import { Sidebar } from "./components/Sidebar";

export default function App() {
  useEffect(() => {
    init();
  }, []);

  return (
    <div className="flex h-full w-full overflow-hidden bg-background text-foreground">
      <Sidebar />
      <ChatPane />
      <ModelModal />
    </div>
  );
}
