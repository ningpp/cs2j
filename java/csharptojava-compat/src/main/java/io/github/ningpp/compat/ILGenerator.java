package io.github.ningpp.compat;

import java.lang.reflect.Method;

/**
 * Stub for System.Reflection.Emit.ILGenerator.
 * This type is not supported on Java; all mutating operations throw UnsupportedOperationException.
 */
public final class ILGenerator {

    public ILGenerator() {
    }

    public void Emit(OpCode opcode) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, byte value) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, short value) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, int value) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, long value) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, float value) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, double value) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, String value) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, Method method) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, LocalBuilder local) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, Label label) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, Label[] labels) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, Class<?> type) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, FieldBuilder field) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, ConstructorBuilder constructor) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, TypeBuilder type) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void Emit(OpCode opcode, java.lang.reflect.Type type) {
        throw new UnsupportedOperationException("ILGenerator.Emit is not supported on Java.");
    }

    public void EmitCall(OpCode opcode, Method method, Class<?>[] optionalParameterTypes) {
        throw new UnsupportedOperationException("ILGenerator.EmitCall is not supported on Java.");
    }

    public Label DefineLabel() {
        return new Label();
    }

    public void MarkLabel(Label label) {
        throw new UnsupportedOperationException("ILGenerator.MarkLabel is not supported on Java.");
    }

    public LocalBuilder DeclareLocal(Class<?> localType) {
        return new LocalBuilder(localType);
    }

    public void BeginExceptionBlock() {
        throw new UnsupportedOperationException("ILGenerator.BeginExceptionBlock is not supported on Java.");
    }

    public void BeginCatchBlock(Class<?> exceptionType) {
        throw new UnsupportedOperationException("ILGenerator.BeginCatchBlock is not supported on Java.");
    }

    public void EndExceptionBlock() {
        throw new UnsupportedOperationException("ILGenerator.EndExceptionBlock is not supported on Java.");
    }
}
