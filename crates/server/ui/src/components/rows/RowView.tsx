import type { UiRow } from "../../types";
import { AssistantRow } from "./AssistantRow";
import { ErrorRow } from "./ErrorRow";
import { InfoRow } from "./InfoRow";
import { ToolRow } from "./ToolRow";
import { UserRow } from "./UserRow";

export function RowView({ row }: { row: UiRow }) {
  switch (row.kind) {
    case "user_text":
      return <UserRow text={row.text} />;
    case "assistant_text":
      return <AssistantRow text={row.text} open={row.open} />;
    case "tool_use":
      return <ToolRow row={row} />;
    case "error":
      return <ErrorRow code={row.code} fatal={row.fatal} message={row.message} />;
    case "info":
      return <InfoRow text={row.text} />;
  }
}
