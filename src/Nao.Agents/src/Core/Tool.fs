namespace Nao.Agents

open System.Threading.Tasks

/// A tool that an agent can invoke
type Tool =
    { Name: string
      Description: string
      Execute: string -> Task<string> }
