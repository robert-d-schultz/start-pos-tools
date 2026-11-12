# Start Pos Tools

Tools for **Total War: WARHAMMER III** start positions. Right now it's a
decompiler: it turns a built `startpos.esf` back into a `.pack` of
`start_pos_*` tables that RPFM can build again.

## Requirements

- [RPFM 5.3+](https://github.com/Frodo45127/rpfm/releases), with the Warhammer 3 game folder set and the dependencies cache generated
- .NET 8 SDK

## Build

```
dotnet build StartPos.slnx
```

Run `src/StartPos.App` for the window, or `src/StartPos.Cli` for the command line.
