namespace ProxyDivert.Core.Routing.Enums;

// What one condition looks at. This is the first combo box on every row of the filter editor, and
// it is what decides which comparison list the second combo box offers.
//
// It exists only for the editor: the condition tree itself says the same thing by the type of the
// node, and that is what gets serialized. Adding a subject here means adding a LeafCondition class
// to go with it — the editor turns one into the other and back.
//
// The numbers are the serialized form of nothing at all, but they are the order of the drop-down:
// append, never renumber.
public enum ConditionSubject
{
    // The file name and the full path of the process.
    ProcessName = 0,

    // The whole command line it was started with.
    CommandLine = 1,
}
