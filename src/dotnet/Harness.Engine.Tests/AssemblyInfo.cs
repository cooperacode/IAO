// StateStore e TaskRegistry compartilham um arquivo de estado fixo (.harness/state.json),
// então os testes precisam rodar em série para não corromperem o estado uns dos outros.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
