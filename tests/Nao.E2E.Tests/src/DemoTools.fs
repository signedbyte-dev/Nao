namespace Nao.E2E.Tests

open System.Threading.Tasks
open Nao.Agents

/// Demo tools for E2E testing
module DemoTools =

    /// A weather lookup tool that returns fake weather data
    let getWeather: Tool =
        { Name = "get_weather"
          Description = "Get the current weather for a location"
          Execute = fun args ->
            Task.FromResult(sprintf "The weather in %s is 18°C and sunny." args) }

    /// A calculator tool that evaluates simple math expressions
    let calculator: Tool =
        { Name = "calculator"
          Description = "Evaluate a math expression"
          Execute = fun args ->
            let result =
                match args.Trim() with
                | "2 + 2" -> "4"
                | "3 * 7" -> "21"
                | "10 / 2" -> "5"
                | "100 - 37" -> "63"
                | expr -> sprintf "Cannot evaluate: %s" expr
            Task.FromResult(result) }

    /// A greeting tool
    let greeter: Tool =
        { Name = "greeter"
          Description = "Generate a greeting for a person"
          Execute = fun args ->
            Task.FromResult(sprintf "Hello, %s! Welcome aboard." args) }
