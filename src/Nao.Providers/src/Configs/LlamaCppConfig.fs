namespace Nao.Providers

/// Configuration for llama.cpp server
type LlamaCppConfig =
    { BaseUrl: string
      NPredict: int option }

    static member Default =
        { BaseUrl = "http://localhost:8080"
          NPredict = None }
