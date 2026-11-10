//go:build !darwin

package main

import (
	"fmt"
	"os"
)

func notify(c *Config, msg string) { fmt.Println(msg) }

func fail(c *Config, msg string) {
	fmt.Fprintln(os.Stderr, msg)
	fmt.Println("Press Enter to close.")
	fmt.Scanln()
	os.Exit(1)
}

func info(c *Config, msg string) { fmt.Println(msg) }

func rosettaProblem() string { return "" }
