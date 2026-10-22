package io.github.ningpp.compat;

import java.lang.reflect.Method;

/**
 * Stub for System.Reflection.Emit.ILGenerator.
 * This type is not supported on Java; all mutating operations throw
 * {@link PlatformNotSupportedException}, mirroring the .NET Core behavior of the
 * Reflection.Emit-based flows that reach it (e.g. Regex.CompileToAssembly, whose
 * save step throws PNSE on .NET Core). UnsupportedOperationException would be the
 * analog of C# InvalidOperationException, which is the wrong contract here.
 */
public final class ILGenerator {

    public ILGenerator() {
    }

    public void Emit(OpCode opcode) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, byte value) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, short value) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, int value) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, long value) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, float value) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, double value) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, String value) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, Method method) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, MethodInfo method) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, ConstructorInfo constructor) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, FieldInfo field) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, LocalBuilder local) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, Label label) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, Label[] labels) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, Class<?> type) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, FieldBuilder field) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, ConstructorBuilder constructor) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, TypeBuilder type) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, java.lang.reflect.Type type) {
        throw new PlatformNotSupportedException("ILGenerator.Emit is not supported on Java.");
    }

    public void EmitCall(OpCode opcode, Method method, Class<?>[] optionalParameterTypes) {
        throw new PlatformNotSupportedException("ILGenerator.EmitCall is not supported on Java.");
    }

    public Label DefineLabel() {
        return new Label();
    }

    public void MarkLabel(Label label) {
        throw new PlatformNotSupportedException("ILGenerator.MarkLabel is not supported on Java.");
    }

    public LocalBuilder DeclareLocal(Class<?> localType) {
        return new LocalBuilder(localType);
    }

    public void BeginExceptionBlock() {
        throw new PlatformNotSupportedException("ILGenerator.BeginExceptionBlock is not supported on Java.");
    }

    public void BeginCatchBlock(Class<?> exceptionType) {
        throw new PlatformNotSupportedException("ILGenerator.BeginCatchBlock is not supported on Java.");
    }

    public void EndExceptionBlock() {
        throw new PlatformNotSupportedException("ILGenerator.EndExceptionBlock is not supported on Java.");
    }
}
